using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Consumes fired triggers and turns each into a basket of order legs. Runs as its own consumer
/// loop off a channel so a slow or failing order call never blocks tick/spread processing
/// upstream.
///
/// "Basket" here means two things:
///  1. Before placing anything, the basket's combined margin requirement is checked against
///     Kite's basket-margin API (which applies the hedge/spread benefit between offsetting near
///     and far legs on the same underlying - see IMarginService.GetBasketMarginAsync) and,
///     if EngineOptions.VerifyMarginBeforeDispatch is on, against the account's actual available
///     margin (GetAvailableMarginAsync). If the account can't cover it, nothing is placed at
///     all - see CheckMarginAsync.
///  2. Legs are then placed hedge-first (see PlaceHedgeFirstAsync): the BUY (long/hedge) leg(s)
///     go in first and only once they've filled are the SELL (short) leg(s) sent, so the short is
///     evaluated by Kite WITH the hedge margin benefit already applied (the low "final" margin)
///     rather than being charged - and potentially rejected for - full naked-short margin. If the
///     hedge doesn't fill, the short is deliberately never placed. IOrderGateway/RateLimiter still
///     paces the actual Kite calls under the rate limit.
///
/// There is deliberately no attempt at a true all-or-nothing (fill everything or nothing at all)
/// basket here, because Kite Connect/NSE simply doesn't offer that for F&amp;O: there's no atomic
/// "place N orders" endpoint (confirmed by Zerodha's own engineering team - even Kite's own
/// Web/App basket feature is just a loop of independent single-leg orders under the hood, and
/// Zerodha's own docs describe baskets partially executing as expected/normal behavior), and the
/// order `validity` parameter only supports DAY/IOC/TTL - no FOK/AON. So instead this minimizes
/// (rather than eliminates) the naked-leg window with three layers:
///  a. Pre-trade margin gating (EngineOptions.VerifyMarginBeforeDispatch / CheckMarginAsync) -
///     if the account can't cover the whole basket, nothing is sent at all.
///  b. Every MARKET-originated leg is sent as a protected IOC LIMIT order (see
///     MarketProtectionService.Protect) - IOC fills what it can immediately and cancels the
///     remainder right away instead of resting, so its outcome is known almost instantly rather than
///     staying ambiguous for a long time.
///  c. KiteOrderGateway.PlaceOrderAsync polls the order's real status after placement instead of
///     trusting mere placement-acceptance as "success" - if a leg's status couldn't be confirmed
///     as terminal in time, it's actively cancelled rather than left resting and unmanaged
///     (CancelStrayOpenOrdersAsync).
///  d. ALWAYS-FILL: an incomplete basket is completed, never unwound. A short is never released
///     bigger than the hedge that actually filled (PlaceHedgeFirstAsync sizes it to the hedge's
///     real fill, not to what was requested), and RetryUnfilledLegsAsync then drives BOTH sides up
///     to the rule's full requested quantity - hedge first, with the short capped each round at
///     whatever the hedge currently holds (ComputeLegTargets), so the position is hedged at every
///     intermediate step rather than only at the end. Giving a filled leg back is deliberately NOT
///     the fallback: it discards the position the rule fired for and pays the spread twice to end
///     up flat. Unwinding remains available as an explicit opt-in
///     (EngineOptions.AutoSquareOffOnPartialFill, off by default); with it off, anything the
///     retries could not fill is left live and logged at Critical (WarnIfLegsLeftUnmatched) rather
///     than silently. The invariant the sizing/capping maintains - the short never exceeds the
///     hedge - is asserted in SpreadTrader.Engine.Tests.PartialFillBalancingTests.
///
/// Any leg configured (or, for square-off, hardcoded) as OrderType=MARKET is converted to a
/// protected LIMIT order before it's sent - see MarketProtectionService.Protect (shared with
/// PositionsController's manual square-off). Kite Connect's API rejects plain MARKET orders for
/// F&amp;O outright ("Market orders without market protection are not allowed via API"), and
/// there's no market_protection parameter on the order-placement endpoint itself to opt back in -
/// a bounded-slippage LIMIT order is the supported workaround.
///
/// Whatever happens (blocked on margin, legs placed/rejected, square-off fired) is published as
/// a BasketExecutionEvent via IOrderExecutionSink, correlated back to the original TriggerEvent
/// by FireId - that's what lets the dashboard's Trigger Activity feed show real buy/sell/success/
/// reject outcomes instead of just "a trigger fired".
/// </summary>
public sealed class OrderDispatchHostedService : BackgroundService
{
    private readonly TriggerEventChannel _triggerChannel;
    private readonly ITriggerRuleStore _ruleStore;
    private readonly IExitTriggerRuleStore _exitRuleStore;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly IOrderGateway _orderGateway;
    private readonly BasketMarginGate _marginGate;
    private readonly IOrderExecutionSink _executionSink;
    private readonly MarketProtectionService _marketProtection;
    private readonly TriggerInFlightTracker _inFlightTracker;
    private readonly EngineOptions _options;
    private readonly ILogger<OrderDispatchHostedService> _logger;

    public OrderDispatchHostedService(
        TriggerEventChannel triggerChannel,
        ITriggerRuleStore ruleStore,
        IExitTriggerRuleStore exitRuleStore,
        ISpreadDefinitionStore spreadStore,
        IOrderGateway orderGateway,
        BasketMarginGate marginGate,
        IOrderExecutionSink executionSink,
        MarketProtectionService marketProtection,
        TriggerInFlightTracker inFlightTracker,
        IOptions<EngineOptions> options,
        ILogger<OrderDispatchHostedService> logger)
    {
        _triggerChannel = triggerChannel;
        _ruleStore = ruleStore;
        _exitRuleStore = exitRuleStore;
        _spreadStore = spreadStore;
        _orderGateway = orderGateway;
        _marginGate = marginGate;
        _executionSink = executionSink;
        _marketProtection = marketProtection;
        _inFlightTracker = inFlightTracker;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var triggerEvent in _triggerChannel.Reader.ReadAllAsync(stoppingToken))
        {
            // Marked in flight for the whole duration of DispatchAsync (margin check, hedge-first
            // placement, partial-fill square-off - everything), cleared in `finally` regardless of
            // outcome, so TriggerInFlightTracker.IsInFlight reflects "still resolving" vs "outcome
            // known" accurately for the bounded multi-shot trigger mode - see
            // SpreadCalculationHostedService.EvaluateTriggersAsync.
            _inFlightTracker.MarkInFlight(triggerEvent.RuleId);
            try
            {
                await DispatchAsync(triggerEvent, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch basket for trigger {RuleId}", triggerEvent.RuleId);
                // Underlying/order kind aren't known here (the exception could have come from
                // anywhere in DispatchAsync) - fall back to the spread id and "Unknown".
                await PublishBlockedAsync(triggerEvent, triggerEvent.SpreadId, "Unknown", $"Dispatch threw: {ex.Message}", stoppingToken);
            }
            finally
            {
                _inFlightTracker.MarkComplete(triggerEvent.RuleId);
            }
        }
    }

    private async Task DispatchAsync(TriggerEvent triggerEvent, CancellationToken stoppingToken)
    {
        // Which store a fired rule is looked up in (and re-persisted to, below and in
        // RestoreRuleIfNothingFilled) follows TriggerEvent.IsExitTrigger, stamped by
        // SpreadCalculationHostedService from whichever of the two stores it evaluated the rule
        // out of - see IExitTriggerRuleStore's doc comment for why entry and exit rules are never
        // in the same store to begin with.
        var ruleStore = triggerEvent.IsExitTrigger ? (ITriggerRuleStore)_exitRuleStore : _ruleStore;
        var rule = ruleStore.Get(triggerEvent.RuleId);
        var spread = _spreadStore.Get(triggerEvent.SpreadId);
        if (rule is null || spread is null)
        {
            _logger.LogWarning("Trigger fired for a rule/spread that no longer exists: {RuleId}/{SpreadId}",
                triggerEvent.RuleId, triggerEvent.SpreadId);
            // Neither rule nor spread is available here, so there's no underlying/leg-count to
            // report - fall back to the spread id as the display name and "Unknown" as the kind.
            await PublishBlockedAsync(triggerEvent, triggerEvent.SpreadId, "Unknown", "Rule or spread was deleted before dispatch ran", stoppingToken);
            return;
        }

        var legPlans = BuildLegs(rule, spread);
        if (legPlans.Count == 0)
        {
            _logger.LogWarning("Rule {RuleId} fired but has no leg actions configured - nothing to place", rule.Id);
            await PublishBlockedAsync(triggerEvent, spread.Underlying, DescribeOrderKind(0), "No leg actions configured for this rule/direction", stoppingToken);
            return;
        }

        var orderKind = DescribeOrderKind(legPlans.Count);

        // Apply market protection before the margin check too, so the basket-margin figure
        // reflects what's actually about to be sent (a MARKET order and its protected-LIMIT
        // equivalent can price margin slightly differently).
        var legs = legPlans.Select(plan => _marketProtection.Protect(plan.Request, plan.InstrumentToken, plan.TickSize, $"Rule {rule.Id}", rule.SlippagePercent)).ToList();

        // EngineOptions.VerifyMarginBeforeDispatch gates whether the margin service is called
        // at all here, not just whether its result can block dispatch - when it's off, this
        // skips straight to placing orders with no basket-margin or available-margin round
        // trips beforehand (Kite's own order entry still enforces margin regardless).
        if (_options.VerifyMarginBeforeDispatch)
        {
            var blockReason = await _marginGate.CheckAsync(rule.Id, legs, stoppingToken);
            if (blockReason is not null)
            {
                await PublishBlockedAsync(triggerEvent, spread.Underlying, orderKind, blockReason, stoppingToken);
                RestoreRuleIfNothingFilled(ruleStore, rule.Id); // nothing was placed at all - don't burn a shot on this
                return; // fail-safe: nothing placed at all
            }
        }

        // Place the hedge (BUY) leg(s) first, confirm they fill, and only then place the short
        // (SELL) leg(s) - see PlaceHedgeFirstAsync and the class doc comment for why. This is what
        // gets the exchange's hedge margin benefit applied to the short instead of it being blocked
        // as a naked short, and keeps the brief between-legs window on the safe (long) side.
        var placements = await PlaceHedgeFirstAsync(legs, rule.Id, stoppingToken);

        // Deliberately NOT gated behind AutoSquareOffOnPartialFill any more. Cancelling a stray
        // resting order and driving an incomplete basket to completion are what keep the account
        // safe under the always-fill policy - they are the primary mechanism now, not a
        // sub-step of the square-off fallback they used to be nested inside.
        {
            // A leg whose fill status came back "OPEN" (the post-placement poll gave up before
            // Kite reached a terminal status - see KiteOrderGateway.PollOrderStatusAsync) is
            // still live on the exchange and could fill later on its own, creating an untracked
            // position with no square-off ever fired for it. Cancel those outright rather than
            // leaving them to resolve unattended - this only ever affects DAY-validity legs
            // (protected IOC legs resolve to a terminal status almost immediately, so they
            // essentially never land here). Runs BEFORE the retry below so a leg is never
            // retried while its original order might still be resting/fillable - that would
            // risk placing two live orders for the same leg at once.
            await CancelStrayOpenOrdersAsync(rule.Id, legs, placements, stoppingToken);

            // Up to EngineOptions.RetryUnfilledLegMaxAttempts more shots at whichever leg(s) still
            // have quantity outstanding, each re-priced fresh off the current market, for just the
            // outstanding quantity, at the same wider slippage band - driving the basket to the
            // full size the rule asked for rather than settling or unwinding. Mutates legs/
            // placements in place, so legResults below sees the final cumulative outcome rather
            // than the original rejection.
            if (_options.RetryUnfilledLegOnPartialFill)
                await RetryUnfilledLegsAsync(rule, legPlans, legs, placements, stoppingToken);
        }

        var legResults = new List<OrderLegResult>(legs.Count);
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var result = placements[i];
            legResults.Add(new OrderLegResult(leg.TradingSymbol, leg.TransactionType, leg.Quantity, result.Success, result.OrderId, result.ErrorMessage, FilledQuantity: result.FilledQuantity, Price: leg.Price, OrderType: leg.OrderType, Status: result.Status, PriceReference: leg.PriceReference, AverageFillPrice: result.AverageFillPrice));

            if (!result.Success)
            {
                _logger.LogError("Basket leg failed for rule {RuleId}: {Symbol} {Side} - {Error}",
                    rule.Id, leg.TradingSymbol, leg.TransactionType, result.ErrorMessage);
            }
        }

        // Captured AFTER the cancel/retry above but BEFORE any square-off cleanup is appended
        // below, so this reflects the final outcome of the ORIGINAL basket legs (including a
        // successful retry) - true only when nothing filled at all (every leg rejected, still
        // rejected after retry, or skipped because the hedge leg didn't fill - see
        // PlaceHedgeFirstAsync). A genuine partial fill that gets auto-squared-off is NOT
        // "nothing filled" - real position was opened (and then unwound), so that attempt
        // legitimately counts and the rule stays fired/consumed.
        var anyLegFilled = legResults.Any(r => r.FilledQuantity > 0);

        if (legPlans.Count > 1)
        {
            // Default policy is ALWAYS-FILL: an incomplete basket is completed by the retries
            // above, never by unwinding the side that did fill - giving back a filled leg throws
            // away the position the rule fired for and pays the spread twice. Unwinding is kept
            // only as an explicit opt-in (EngineOptions.AutoSquareOffOnPartialFill, off by
            // default) for anyone who would rather be flat than exposed.
            if (_options.AutoSquareOffOnPartialFill)
                legResults.AddRange(await SquareOffPartialFillAsync(rule, legPlans, placements, stoppingToken));
            else
                WarnIfLegsLeftUnmatched(rule.Id, legPlans, placements);
        }

        if (!anyLegFilled)
        {
            RestoreRuleIfNothingFilled(ruleStore, rule.Id); // fully rejected/unfilled - don't burn a shot on this
        }
        else if (rule.UnfilledAttempts > 0)
        {
            // A genuine fill breaks the consecutive-miss streak that counts toward
            // EngineOptions.MaxUnfilledDispatchAttempts - see RestoreRuleIfNothingFilled.
            rule.UnfilledAttempts = 0;
            ruleStore.Upsert(rule);
        }

        await _executionSink.PublishAsync(
            new BasketExecutionEvent(triggerEvent.FireId, rule.Id, spread.Id, spread.Underlying, orderKind, legResults, MarginBlocked: false, BlockedReason: null, DateTimeOffset.UtcNow, SpreadValueAtTrigger: triggerEvent.SpreadValueAtTrigger),
            stoppingToken);
    }

    /// <summary>Undoes the "this rule has fired" bookkeeping (IsArmed/Enabled/FireCount) that
    /// SpreadCalculationHostedService.EvaluateTriggersAsync applies BEFORE the actual order
    /// outcome is known, for the case where nothing from this attempt actually filled - the whole
    /// basket was blocked on margin, or every leg was rejected/skipped (e.g. the hedge leg didn't
    /// fill so the sell leg was never even sent - see PlaceHedgeFirstAsync). An attempt like that
    /// didn't open or change any real position, so it shouldn't cost the rule its one shot
    /// (OneShot) or one of its bounded shots (MaxFireCount) either - re-arms/re-enables it (and
    /// gives the shot back) so the next crossing gets a genuine attempt instead of silently doing
    /// nothing because the rule already looks "used up" from a fire that never actually placed
    /// anything. Deliberately NOT called for a genuine partial fill that gets auto-squared-off -
    /// see the anyLegFilled comment in DispatchAsync. LastFiredAt is deliberately left untouched
    /// so EngineOptions.MinFireIntervalPerRuleMs still throttles an immediate retry (e.g. against
    /// a persistently-insufficient-margin condition that won't resolve instantly).
    ///
    /// This is also the only place TriggerRule.UnfilledAttempts is incremented. Re-arming this
    /// rule unconditionally forever would mean a condition that can NEVER fill (chronic
    /// insufficient margin, a frozen/delisted instrument, a persistently rejected order) re-fires
    /// every single time the spread re-crosses the threshold, indefinitely. Once UnfilledAttempts
    /// reaches EngineOptions.MaxUnfilledDispatchAttempts, this disarms AND disables the rule
    /// instead of giving it another shot - it stays that way until a human re-enables it (see
    /// TriggerRuleControllerBase.Enable, which also resets UnfilledAttempts back to 0). A fill on
    /// any later attempt resets the counter too - see the anyLegFilled branch in DispatchAsync -
    /// so an occasional reject on an otherwise-healthy rule never counts toward the cap.</summary>
    private void RestoreRuleIfNothingFilled(ITriggerRuleStore ruleStore, string ruleId)
    {
        var rule = ruleStore.Get(ruleId);
        if (rule is null) return; // deleted since it fired - nothing to restore

        rule.UnfilledAttempts++;

        if (rule.UnfilledAttempts >= _options.MaxUnfilledDispatchAttempts)
        {
            rule.IsArmed = false;
            rule.Enabled = false;
            ruleStore.Upsert(rule);
            _logger.LogError(
                "Rule {RuleId}: {Attempts} consecutive fire(s) with nothing filled (blocked or fully rejected) - disarming instead of retrying forever. Re-enable it manually once the underlying issue (margin, instrument status, etc.) is resolved.",
                ruleId, rule.UnfilledAttempts);
            return;
        }

        if (!rule.IsArmed) rule.IsArmed = true;
        if (!rule.Enabled) rule.Enabled = true;
        if (!rule.OneShot && rule.MaxFireCount > 0 && rule.FireCount > 0) rule.FireCount--;
        ruleStore.Upsert(rule);

        _logger.LogInformation(
            "Rule {RuleId}: nothing filled on this fire (blocked or fully rejected, {Attempts}/{Max} consecutive) - re-armed/re-enabled so it can try again.",
            ruleId, rule.UnfilledAttempts, _options.MaxUnfilledDispatchAttempts);
    }

    /// <summary>Places the basket hedge-first: all BUY (long/hedge) legs go in first and only once
    /// they've resolved to a fill are the SELL (short) legs sent. Returns an OrderResult[] aligned
    /// to the input <paramref name="legs"/> order (same indices), so the caller's result mapping,
    /// stray-order cancel, and partial-fill square-off logic are unchanged.
    ///
    /// Why sequence instead of firing everything at once: at placement time Kite blocks margin
    /// per order. A naked short leg reaching Kite before its hedge exists is charged the full
    /// un-hedged SPAN+exposure (in Kite's own basket-margin example, ~3x the hedged figure), so an
    /// account funded only for the hedged spread can have its short leg rejected outright. Getting
    /// the buy leg in as a position first means the short is evaluated WITH the hedge benefit (the
    /// low "final" margin) and goes through. The short side is also the risky/expensive side to be
    /// caught naked on, so doing buy-first keeps the brief inter-leg window on the safe long side.
    ///
    /// If a hedge leg fails to fill, the short legs are deliberately NOT sent (placing a naked
    /// short is exactly what this ordering exists to avoid) - they come back as skipped results,
    /// and any hedge that did fill is flattened by the caller's partial-fill square-off.</summary>
    private async Task<OrderResult[]> PlaceHedgeFirstAsync(List<OrderLegRequest> legs, string ruleId, CancellationToken cancellationToken)
    {
        var results = new OrderResult[legs.Count];
        var buyIndices = new List<int>();
        var sellIndices = new List<int>();
        for (var i = 0; i < legs.Count; i++)
            (string.Equals(legs[i].TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) ? buyIndices : sellIndices).Add(i);

        // 1. Hedge (BUY) legs first, together - buys never need a pre-existing position, so there's
        //    no reason to serialize them among themselves; the sequencing that matters is buys
        //    before sells, not buy before buy.
        if (buyIndices.Count > 0)
        {
            var buyResults = await Task.WhenAll(buyIndices.Select(i => _orderGateway.PlaceOrderAsync(legs[i], cancellationToken)));
            for (var k = 0; k < buyIndices.Count; k++)
                results[buyIndices[k]] = buyResults[k];
        }

        if (sellIndices.Count == 0)
            return results;

        // 2. Short (SELL) legs, sized to the hedge that ACTUALLY filled - not to what was
        //    requested. OrderResult.Success only means "at least one lot filled", so a hedge that
        //    filled 1 of 2 lots used to release the full 2-lot short: the account ends up short 2
        //    against a 1-lot hedge, i.e. genuinely naked on the difference, for as long as it takes
        //    the cleanup below to notice. Sizing the short to min(filled across the hedge legs)
        //    means the exposure released can never exceed the hedge protecting it. A sell-only rule
        //    (no buy legs at all) has no hedge to size against and goes out as requested.
        var hedgeQuantity = buyIndices.Count == 0 ? int.MaxValue : buyIndices.Min(i => results[i].FilledQuantity);

        if (hedgeQuantity <= 0)
        {
            _logger.LogError(
                "Rule {RuleId}: hedge (BUY) leg did not fill - NOT placing the SELL leg(s) to avoid opening a naked short. Any hedge that did fill will be squared off.",
                ruleId);
            foreach (var i in sellIndices)
                results[i] = new OrderResult
                {
                    Success = false,
                    FilledQuantity = 0,
                    Status = "SKIPPED",
                    ErrorMessage = "Skipped: hedge (BUY) leg did not fill, so the SELL leg was not placed (would have been a naked short).",
                };
            return results;
        }

        var sellRequests = sellIndices
            .Select(i => hedgeQuantity < legs[i].Quantity ? WithQuantity(legs[i], hedgeQuantity) : legs[i])
            .ToList();

        for (var k = 0; k < sellIndices.Count; k++)
        {
            if (sellRequests[k].Quantity < legs[sellIndices[k]].Quantity)
            {
                _logger.LogWarning(
                    "Rule {RuleId}: hedge (BUY) only filled {HedgeFilled} of {Requested} lot(s), so {Symbol} SELL is being sized down to {SellQty} lot(s) to stay fully hedged rather than opening a naked short on the difference.",
                    ruleId, hedgeQuantity, legs[sellIndices[k]].Quantity, legs[sellIndices[k]].TradingSymbol, sellRequests[k].Quantity);
            }
        }

        var sellResults = await Task.WhenAll(sellRequests.Select(r => _orderGateway.PlaceOrderAsync(r, cancellationToken)));
        for (var k = 0; k < sellIndices.Count; k++)
            results[sellIndices[k]] = sellResults[k];

        return results;
    }

    /// <summary>"Basket" for a real multi-leg spread order, "Single leg" for a rule configured
    /// to act on only one side, "None" when a rule fired but resolved to zero leg actions
    /// (e.g. auto-direction couldn't be determined) - always reported to the dashboard
    /// regardless of whether the order(s) actually succeeded, so the Trigger Activity feed
    /// never shows a fire with no context about what kind of order it was.</summary>
    private static string DescribeOrderKind(int legCount) => legCount switch
    {
        0 => "None",
        1 => "Single leg",
        _ => "Basket",
    };

    private Task PublishBlockedAsync(TriggerEvent triggerEvent, string underlying, string orderKind, string reason, CancellationToken cancellationToken) =>
        _executionSink.PublishAsync(
            new BasketExecutionEvent(triggerEvent.FireId, triggerEvent.RuleId, triggerEvent.SpreadId, underlying, orderKind, Array.Empty<OrderLegResult>(), MarginBlocked: true, BlockedReason: reason, DateTimeOffset.UtcNow, SpreadValueAtTrigger: triggerEvent.SpreadValueAtTrigger),
            cancellationToken);

    /// <summary>Cancels any leg whose fill status couldn't be confirmed as terminal in time
    /// (Status == "OPEN" - see KiteOrderGateway.PollOrderStatusAsync) so it doesn't sit resting
    /// on the exchange, unmanaged, potentially filling later and creating a naked position that
    /// nothing here ever reacts to. Runs unconditionally (independent of whether any other leg
    /// filled) since a stray open order is a hygiene/risk issue on its own, not just a partial-
    /// fill cleanup concern - see SquareOffPartialFillAsync for the "something else already
    /// filled, flatten it" case this complements.</summary>
    private async Task CancelStrayOpenOrdersAsync(string ruleId, List<OrderLegRequest> legs, OrderResult[] results, CancellationToken cancellationToken)
    {
        for (var i = 0; i < legs.Count; i++)
        {
            var result = results[i];
            if (!string.Equals(result.Status, "OPEN", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(result.OrderId))
                continue;

            _logger.LogWarning(
                "Rule {RuleId}: {Symbol} order {OrderId} is still open/unconfirmed - cancelling it rather than leaving it to resolve unattended.",
                ruleId, legs[i].TradingSymbol, result.OrderId);

            var cancelled = await _orderGateway.CancelOrderAsync(result.OrderId, legs[i].Variety, cancellationToken);
            if (cancelled)
            {
                // Confirmed cancelled - nothing left resting for this leg on the exchange, so
                // it's now genuinely safe for RetryUnfilledLegsAsync to place a fresh order for
                // it without risking the original also filling later and double-executing this
                // leg. Left as "OPEN" if cancellation itself failed/couldn't be confirmed - see
                // RetryUnfilledLegsAsync, which deliberately treats "OPEN" as not retry-eligible.
                result.Success = false;
                result.Status = "CANCELLED";
                result.ErrorMessage = "Cancelled - status could not be confirmed as filled/rejected in time.";
            }
        }
    }

    /// <summary>Up to EngineOptions.RetryUnfilledLegMaxAttempts attempts, each at the same wider
    /// slippage band and each re-priced fresh off the latest market price, for whichever leg(s)
    /// of a genuinely partial fill still have quantity outstanding - either a leg that was fully
    /// rejected/cancelled (0 filled) OR one that itself only PARTIALLY filled (e.g. 1 of 2 lots,
    /// the IOC protection filled what it could and cancelled the rest) - tried BEFORE
    /// SquareOffPartialFillAsync unwinds whatever else already filled, since a fill (even at
    /// worse pricing) keeps the intended hedged spread intact, which is strictly better than
    /// flattening a leg that DID fill and ending the attempt with no position at all. Only fires
    /// for a genuine partial fill (at least one leg already has some fill - nothing to protect
    /// otherwise) and only retries legs that are actually safe to retry:
    ///  - NOT "SKIPPED" (the hedge's sell/buy partner deliberately never sent because the hedge
    ///    leg itself didn't fill - see PlaceHedgeFirstAsync). Retrying that would defeat the
    ///    entire point of hedge-first sequencing: it would risk placing a naked short/long
    ///    exactly like PlaceHedgeFirstAsync exists to avoid.
    ///  - NOT "OPEN" - either the original order (CancelStrayOpenOrdersAsync, which runs
    ///    immediately before this, couldn't confirm it was actually cancelled) or a PRIOR retry
    ///    attempt within this same call that itself came back unconfirmed. Retrying either risks
    ///    that still-maybe-live order ALSO filling later, double-executing this leg - so once a
    ///    leg lands on "OPEN" it's left alone for the rest of this call rather than retried again.
    /// Each retry only asks for the OUTSTANDING quantity (requested minus however much has
    /// already filled across the original attempt and any earlier retries this call), not the
    /// full original quantity again - a leg that already got 1 of 2 lots is retried for the
    /// remaining 1, not re-sent for 2. Fills accumulate: FilledQuantity and AverageFillPrice on
    /// the leg's OrderResult become the running total/weighted-average across every attempt, so a
    /// leg that fills 1 lot on the original attempt and 1 more on retry #1 reports FilledQuantity
    /// = 2, not 1. Every attempt reuses the SAME wider slippage (rule.SlippagePercent *
    /// RetryUnfilledLegSlippageMultiplier, e.g. 3x) rather than widening further each round - only
    /// the reference price and the outstanding quantity are refreshed each attempt, not the band
    /// itself. After RetryUnfilledLegMaxAttempts attempts a leg still has quantity outstanding,
    /// this gives up on the remainder and lets the caller's balancing pass take over, which unwinds
    /// whatever ended up unmatched - including the case where the legs finish at genuinely
    /// different filled quantities (leg A at 2, leg B at 1), which is now reduced to the matched
    /// quantity rather than left standing. Mutates <paramref name="legs"/>/<paramref name="placements"/> in place
    /// at the retried index/indices so every caller downstream (legResults,
    /// SquareOffPartialFillAsync) sees the final cumulative outcome instead of the original
    /// rejection.</summary>
    private async Task RetryUnfilledLegsAsync(TriggerRule rule, List<LegPlan> legPlans, List<OrderLegRequest> legs, OrderResult[] placements, CancellationToken cancellationToken)
    {
        var anythingFilled = placements.Any(p => p.FilledQuantity > 0);
        if (!anythingFilled)
            return; // nothing filled at all - no exposure to complete; the rule is re-armed and gets a fresh attempt on the next crossing

        var widerSlippage = rule.SlippagePercent * _options.RetryUnfilledLegSlippageMultiplier;
        var maxAttempts = Math.Max(1, _options.RetryUnfilledLegMaxAttempts);

        var isSell = legPlans
            .Select(p => !string.Equals(p.Request.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Each round re-derives what every leg is allowed to reach for RIGHT NOW, because the
            // hedge may have grown since the last round. Buys are always chased to the full
            // requested quantity - the aim is to COMPLETE the spread, not to settle for a smaller
            // one. Sells are capped at whatever the hedge currently holds, so topping up the short
            // can never overshoot the long protecting it even mid-way through filling. Buys are
            // therefore processed before sells within each round, exactly like the initial
            // placement - see ComputeLegTargets.
            var targets = ComputeLegTargets(
                legPlans.Select(p => p.Request.Quantity).ToArray(),
                placements.Select(p => p.FilledQuantity).ToArray(),
                isSell);

            var pending = new List<int>();
            for (var i = 0; i < placements.Length; i++)
            {
                if (placements[i].FilledQuantity >= targets[i]) continue; // already at target - nothing outstanding
                // "OPEN" means the original order (or an earlier retry) could not be confirmed
                // cancelled, so it may still be live - placing another would risk double-executing
                // this leg. Left alone for the rest of this call.
                if (string.Equals(placements[i].Status, "OPEN", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Add(i);
            }

            // Buys first, then sells - so a sell is only ever topped up against a hedge that
            // already exists in this same round.
            pending.Sort((a, b) => isSell[a].CompareTo(isSell[b]));

            if (pending.Count == 0)
                break; // every leg has reached its target - the basket is complete and matched

            foreach (var i in pending)
            {
                var plan = legPlans[i];
                var already = placements[i];
                var outstandingQty = targets[i] - already.FilledQuantity;

                _logger.LogWarning(
                    "Rule {RuleId}: {Symbol} {Side} has {Outstanding}/{Target} lot(s) outstanding at {OriginalSlippage}% slippage ({Status}) - retry {Attempt}/{MaxAttempts} at {WiderSlippage}%.",
                    rule.Id, plan.Request.TradingSymbol, plan.Request.TransactionType, outstandingQty, targets[i], rule.SlippagePercent, already.Status, attempt, maxAttempts, widerSlippage);

                // Re-priced fresh off whatever the tick store has right now (Protect reads it at
                // call time) rather than reusing a stale reference from the original attempt or an
                // earlier retry - the whole point of a wider band is to reach a price the market
                // may have since moved to/past. Only the OUTSTANDING quantity is requested, not
                // the full original quantity again.
                var outstandingRequest = WithQuantity(plan.Request, outstandingQty);
                var retryLeg = _marketProtection.Protect(outstandingRequest, plan.InstrumentToken, plan.TickSize, $"Rule {rule.Id} retry {attempt}", widerSlippage);
                var retryResult = await _orderGateway.PlaceOrderAsync(retryLeg, cancellationToken);

                var combinedFilled = already.FilledQuantity + retryResult.FilledQuantity;
                var combinedAvgPrice = CombineAverageFillPrice(already.FilledQuantity, already.AverageFillPrice, retryResult.FilledQuantity, retryResult.AverageFillPrice);

                // Report the leg's ORIGINAL total requested quantity (not just this attempt's
                // outstanding remainder) so legResults/the dashboard still show "requested 2,
                // filled 1", not "requested 1, filled 1".
                legs[i] = WithQuantity(retryLeg, plan.Request.Quantity);
                placements[i] = new OrderResult
                {
                    Success = combinedFilled > 0,
                    OrderId = retryResult.OrderId ?? already.OrderId,
                    ErrorMessage = retryResult.ErrorMessage,
                    FilledQuantity = combinedFilled,
                    Status = retryResult.Status,
                    AverageFillPrice = combinedAvgPrice,
                };

                if (combinedFilled >= targets[i])
                {
                    _logger.LogInformation(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} completed {Symbol} {Side} - {Filled}/{Target} lot(s) now filled at {WiderSlippage}% slippage -> order {OrderId}",
                        rule.Id, attempt, maxAttempts, plan.Request.TradingSymbol, plan.Request.TransactionType, combinedFilled, targets[i], widerSlippage, retryResult.OrderId);
                }
                else if (string.Equals(retryResult.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} for {Symbol} {Side} came back OPEN/unconfirmed - leaving it as-is rather than risking a further retry double-placing it.",
                        rule.Id, attempt, maxAttempts, plan.Request.TradingSymbol, plan.Request.TransactionType);
                }
                else
                {
                    var lastAttempt = attempt == maxAttempts;
                    _logger.LogWarning(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} at {WiderSlippage}% still leaves {Symbol} {Side} at {Filled}/{Target} lot(s) - {Error}.{Next}",
                        rule.Id, attempt, maxAttempts, widerSlippage, plan.Request.TradingSymbol, plan.Request.TransactionType, combinedFilled, targets[i], retryResult.ErrorMessage,
                        lastAttempt ? " No retries left." : " Trying again for the remainder.");
                }
            }
        }
    }

    /// <summary>
    /// What each leg is allowed to hold right now, given how much has filled so far.
    ///
    /// BUY (hedge) legs are always chased to their full requested quantity: the objective is to
    /// COMPLETE the spread the rule asked for, never to settle for a smaller one or to unwind back
    /// out of it.
    ///
    /// SELL (short) legs are capped at whatever the hedge currently holds - min(requested, hedge
    /// filled). That cap is what keeps the position hedged at every intermediate step while the
    /// basket is still being filled in: the short can only ever grow as fast as the long protecting
    /// it, so there is no moment where topping up the short overshoots the hedge. As the hedge
    /// fills across retry rounds, the cap rises with it and the short is topped up to follow, until
    /// both sides reach the requested quantity. A sell-only rule has no hedge to cap against and
    /// simply goes to its requested quantity.
    ///
    /// Pure/static and separated from the I/O around it so the rule that decides whether the
    /// account can end up unhedged is unit-testable on its own.
    /// </summary>
    internal static int[] ComputeLegTargets(IReadOnlyList<int> requestedQuantities, IReadOnlyList<int> filledQuantities, IReadOnlyList<bool> isSell)
    {
        var targets = new int[requestedQuantities.Count];

        var hedgeFilled = int.MaxValue;
        for (var i = 0; i < isSell.Count; i++)
            if (!isSell[i]) hedgeFilled = Math.Min(hedgeFilled, filledQuantities[i]);

        for (var i = 0; i < requestedQuantities.Count; i++)
        {
            targets[i] = isSell[i] && hedgeFilled != int.MaxValue
                ? Math.Min(requestedQuantities[i], hedgeFilled)
                : requestedQuantities[i];
        }

        return targets;
    }

    /// <summary>
    /// How many lots of each leg have to be given back for the basket to end up balanced. The
    /// matched quantity a multi-leg basket actually achieved is the MINIMUM filled across its legs;
    /// anything a leg holds above that minimum is unhedged and has to be unwound.
    ///
    /// This generalises the old "if any leg didn't fill, flatten every leg that did" rule, which
    /// only ever recognised the all-or-nothing case and silently accepted the far more common
    /// lopsided one. Both now fall out of the same arithmetic:
    ///   - one leg filled, the other rejected -> min is 0 -> the whole filled leg is unwound
    ///     (identical to the previous behaviour), and
    ///   - legs filled 2 and 1 -> min is 1 -> exactly the 1 excess lot is unwound, leaving a real
    ///     1-lot hedged spread instead of a 1-lot naked position nothing ever noticed.
    ///
    /// Pure/static for the same testability reason as <see cref="ComputeRetryTargets"/>.
    /// </summary>
    internal static int[] ComputeExcessQuantities(IReadOnlyList<int> filledQuantities)
    {
        var excess = new int[filledQuantities.Count];
        if (filledQuantities.Count < 2) return excess; // a single leg has no partner to be unbalanced against

        var matched = int.MaxValue;
        for (var i = 0; i < filledQuantities.Count; i++)
            matched = Math.Min(matched, filledQuantities[i]);

        for (var i = 0; i < filledQuantities.Count; i++)
            excess[i] = filledQuantities[i] - matched;

        return excess;
    }

    /// <summary>Clones <paramref name="source"/> with a different Quantity - used by
    /// RetryUnfilledLegsAsync both to build a retry request for just the outstanding remainder of
    /// a partially-filled leg, and to restore the ORIGINAL total quantity on the leg reported back
    /// to the caller (legResults/dashboard should show "requested 2", not "requested 1" just
    /// because the last retry only asked for the 1 remaining lot).</summary>
    private static OrderLegRequest WithQuantity(OrderLegRequest source, int quantity) => new()
    {
        TradingSymbol = source.TradingSymbol,
        Exchange = source.Exchange,
        InstrumentToken = source.InstrumentToken,
        TransactionType = source.TransactionType,
        Quantity = quantity,
        Product = source.Product,
        OrderType = source.OrderType,
        Price = source.Price,
        PriceReference = source.PriceReference,
        Validity = source.Validity,
        Variety = source.Variety,
        Tag = source.Tag,
    };

    /// <summary>Fill-weighted average price across two fills of the same leg (e.g. 1 lot at the
    /// original attempt's price plus 1 more lot at a retry's wider-slippage price) - used by
    /// RetryUnfilledLegsAsync to keep OrderResult.AverageFillPrice a true average of everything
    /// that's actually filled for the leg so far, not just the most recent attempt's price.
    /// Falls back to whichever side has a fill when the other side has none (nothing to
    /// average against), and to null when neither side has filled anything yet.</summary>
    private static decimal? CombineAverageFillPrice(int qtyA, decimal? avgA, int qtyB, decimal? avgB)
    {
        if (qtyA > 0 && avgA is { } priceA && qtyB > 0 && avgB is { } priceB)
            return (priceA * qtyA + priceB * qtyB) / (qtyA + qtyB);
        if (qtyB > 0 && avgB is { } onlyB)
            return onlyB;
        if (qtyA > 0 && avgA is { } onlyA)
            return onlyA;
        return null;
    }

    /// <summary>
    /// Under the always-fill policy nothing is unwound, so an imbalance the retries could not close
    /// (the market simply would not fill that leg within EngineOptions.RetryUnfilledLegMaxAttempts)
    /// is left standing as a real, unhedged position. That must never pass silently: this logs it
    /// at Critical with the exact leftover quantity, so it shows up as an alert rather than being
    /// something only a later look at the position book would reveal. Logged per leg holding more
    /// than the matched quantity - see ComputeExcessQuantities for how that is derived.
    /// </summary>
    private void WarnIfLegsLeftUnmatched(string ruleId, List<LegPlan> legPlans, OrderResult[] results)
    {
        var excess = ComputeExcessQuantities(results.Select(r => r.FilledQuantity).ToArray());
        if (excess.Sum() == 0) return; // basket completed matched - nothing to flag

        for (var i = 0; i < legPlans.Count; i++)
        {
            if (excess[i] <= 0) continue;
            _logger.LogCritical(
                "Rule {RuleId}: {Symbol} {Side} is left with {Excess} UNHEDGED lot(s) that retries could not fill against ({Fills} across the basket). " +
                "Nothing was unwound (always-fill policy - AutoSquareOffOnPartialFill is off), so this position is live and needs manual attention.",
                ruleId, legPlans[i].Request.TradingSymbol, legPlans[i].Request.TransactionType, excess[i],
                string.Join("/", results.Select(r => r.FilledQuantity)));
        }
    }

    /// <summary>Brings the basket back to a MATCHED position by unwinding whatever quantity ended
    /// up unhedged, since a lone (or merely oversized) near/far month future is a completely
    /// different - and far more margin-expensive - risk than the intended hedged spread. See
    /// EngineOptions.AutoSquareOffOnPartialFill and ComputeExcessQuantities, which decides the
    /// amount: the matched quantity is the minimum filled across the legs, and anything a leg holds
    /// above that is given back. That covers both the all-or-nothing case (one leg filled, the
    /// other rejected -> the whole filled leg is unwound) and the lopsided one (legs at 2 and 1 ->
    /// just the 1 excess lot is unwound, keeping a real 1-lot spread) with the same arithmetic.
    /// Fires an opposite-direction order for each excess amount
    /// immediately (also passed through MarketProtectionService, same as any other MARKET leg -
    /// this is cleanup, not a rate-sensitive trade, but Kite still rejects a raw MARKET order
    /// here too); if even the square-off fails, that's logged as Critical since it means the
    /// account is left with a naked position needing manual attention. Returns the square-off
    /// attempts (IsSquareOff=true) so they're included in the published BasketExecutionEvent
    /// alongside the original legs. Takes the full TriggerRule (not just its id) so the flatten
    /// leg can be protected with the SAME SlippagePercent the rule fired with, rather than some
    /// separate default - this is cleanup for a basket that rule just placed, not a new,
    /// independently-configured order.</summary>
    private async Task<List<OrderLegResult>> SquareOffPartialFillAsync(TriggerRule rule, List<LegPlan> legPlans, OrderResult[] results, CancellationToken cancellationToken)
    {
        var ruleId = rule.Id;
        var squareOffResults = new List<OrderLegResult>();

        // Unwind exactly the UNMATCHED quantity on each leg - see ComputeExcessQuantities. Driven
        // off OrderResult.FilledQuantity (what genuinely filled) rather than what was requested, so
        // an IOC leg that only partly filled before the rest was cancelled is unwound for the real
        // amount.
        var excess = ComputeExcessQuantities(results.Select(r => r.FilledQuantity).ToArray());
        var totalExcess = excess.Sum();
        if (totalExcess == 0) return squareOffResults; // legs are already matched (or all empty) - nothing unhedged to unwind

        var matchedQuantity = results.Min(r => r.FilledQuantity);
        if (matchedQuantity > 0)
        {
            _logger.LogError(
                "Rule {RuleId} basket filled UNEVENLY ({Fills}) - keeping the matched {Matched} lot(s) as a hedged spread and unwinding the {Excess} unmatched lot(s), which would otherwise sit naked.",
                ruleId, string.Join("/", results.Select(r => r.FilledQuantity)), matchedQuantity, totalExcess);
        }
        else
        {
            _logger.LogError(
                "Rule {RuleId} basket PARTIALLY FILLED - {FilledCount}/{TotalCount} leg(s) actually filled, leaving a naked, unhedged position. Auto-squaring off the filled leg(s) now.",
                ruleId, results.Count(r => r.FilledQuantity > 0), legPlans.Count);
        }

        for (var i = 0; i < legPlans.Count; i++)
        {
            if (excess[i] <= 0) continue;

            var plan = legPlans[i];
            var leg = plan.Request;
            var flattenLeg = new OrderLegRequest
            {
                TradingSymbol = leg.TradingSymbol,
                Exchange = leg.Exchange,
                InstrumentToken = leg.InstrumentToken,
                TransactionType = leg.TransactionType == "BUY" ? "SELL" : "BUY",
                Quantity = excess[i], // only the unmatched remainder, so a genuinely hedged portion is never flattened along with it
                Product = leg.Product,
                OrderType = "MARKET", // this is risk cleanup, not a rate-sensitive trade - MarketProtectionService still converts it to a protected LIMIT before it's sent
                Tag = leg.Tag,
            };

            var flatten = _marketProtection.Protect(flattenLeg, plan.InstrumentToken, plan.TickSize, $"Rule {ruleId} auto-square-off", rule.SlippagePercent);
            var result = await _orderGateway.PlaceOrderAsync(flatten, cancellationToken);
            squareOffResults.Add(new OrderLegResult(flatten.TradingSymbol, flatten.TransactionType, flatten.Quantity, result.Success, result.OrderId, result.ErrorMessage, IsSquareOff: true, FilledQuantity: result.FilledQuantity, Price: flatten.Price, OrderType: flatten.OrderType, Status: result.Status, PriceReference: flatten.PriceReference, AverageFillPrice: result.AverageFillPrice));

            if (result.Success)
            {
                _logger.LogWarning("Rule {RuleId}: auto-squared off {Symbol} ({Side} x{Qty}) -> order {OrderId}",
                    ruleId, flatten.TradingSymbol, flatten.TransactionType, flatten.Quantity, result.OrderId);
            }
            else
            {
                _logger.LogCritical(
                    "Rule {RuleId}: FAILED to auto-square-off {Symbol} ({Side} x{Qty}) - {Error}. Manual intervention required, position is still naked.",
                    ruleId, flatten.TradingSymbol, flatten.TransactionType, flatten.Quantity, result.ErrorMessage);
            }
        }

        return squareOffResults;
    }

    /// <summary>An order leg paired with the instrument token it prices off of and its tick
    /// size - BuildLegs knows both (spread.NearLeg/FarLeg.InstrumentToken / .TickSize). The token
    /// is also copied onto OrderLegRequest itself (brokers that address orders by security id
    /// rather than tradingsymbol need it - see OrderLegRequest.InstrumentToken); tick size stays
    /// here because only market protection uses it - see MarketProtectionService.Protect.</summary>
    private readonly record struct LegPlan(OrderLegRequest Request, uint InstrumentToken, decimal TickSize);

    private static List<LegPlan> BuildLegs(TriggerRule rule, SpreadDefinition spread)
    {
        var (nearAction, farAction) = (rule.NearLegAction, rule.FarLegAction);

        var legs = new List<LegPlan>();
        var tag = BuildTag(rule.Id);

        if (nearAction != TriggerLegAction.None)
        {
            legs.Add(new LegPlan(
                new OrderLegRequest
                {
                    TradingSymbol = spread.NearLeg.TradingSymbol,
                    Exchange = spread.NearLeg.Exchange,
                    InstrumentToken = spread.NearLeg.InstrumentToken,
                    TransactionType = nearAction == TriggerLegAction.Buy ? "BUY" : "SELL",
                    Quantity = rule.Quantity,
                    Product = rule.Product,
                    OrderType = rule.OrderType,
                    Tag = tag,
                },
                spread.NearLeg.InstrumentToken,
                spread.NearLeg.TickSize));
        }

        if (farAction != TriggerLegAction.None)
        {
            legs.Add(new LegPlan(
                new OrderLegRequest
                {
                    TradingSymbol = spread.FarLeg.TradingSymbol,
                    Exchange = spread.FarLeg.Exchange,
                    InstrumentToken = spread.FarLeg.InstrumentToken,
                    TransactionType = farAction == TriggerLegAction.Buy ? "BUY" : "SELL",
                    Quantity = rule.Quantity,
                    Product = rule.Product,
                    OrderType = rule.OrderType,
                    Tag = tag,
                },
                spread.FarLeg.InstrumentToken,
                spread.FarLeg.TickSize));
        }

        return legs;
    }

    private static string BuildTag(string ruleId)
    {
        // Order tags/correlation ids must be alphanumeric and short (Kite caps them at 20 chars,
        // Dhan's correlationId at 30 and restricts it to alphanumerics plus space/underscore/
        // hyphen) - 20 alphanumerics satisfies both.
        var raw = "str" + new string(ruleId.Where(char.IsLetterOrDigit).ToArray());
        return raw.Length > 20 ? raw[..20] : raw;
    }
}
