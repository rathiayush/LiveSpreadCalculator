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
///     trusting mere placement-acceptance as "success" - if a leg is confirmed filled while
///     another wasn't, the filled one is immediately flattened (SquareOffPartialFillAsync); if a
///     leg's status couldn't be confirmed as terminal in time, it's actively cancelled rather
///     than left resting and unmanaged (CancelStrayOpenOrdersAsync).
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
    private readonly IMarginService _marginService;
    private readonly IOrderExecutionSink _executionSink;
    private readonly MarketProtectionService _marketProtection;
    private readonly TriggerInFlightTracker _inFlightTracker;
    private readonly EngineOptions _options;
    private readonly McxOptions _mcx;
    private readonly ILogger<OrderDispatchHostedService> _logger;

    public OrderDispatchHostedService(
        TriggerEventChannel triggerChannel,
        ITriggerRuleStore ruleStore,
        IExitTriggerRuleStore exitRuleStore,
        ISpreadDefinitionStore spreadStore,
        IOrderGateway orderGateway,
        IMarginService marginService,
        IOrderExecutionSink executionSink,
        MarketProtectionService marketProtection,
        TriggerInFlightTracker inFlightTracker,
        IOptions<EngineOptions> options,
        McxOptions mcx,
        ILogger<OrderDispatchHostedService> logger)
    {
        _triggerChannel = triggerChannel;
        _ruleStore = ruleStore;
        _exitRuleStore = exitRuleStore;
        _spreadStore = spreadStore;
        _orderGateway = orderGateway;
        _marginService = marginService;
        _executionSink = executionSink;
        _marketProtection = marketProtection;
        _inFlightTracker = inFlightTracker;
        _options = options.Value;
        _mcx = mcx;
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
            var blockReason = await CheckMarginAsync(rule.Id, legs, stoppingToken);
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

        if (_options.AutoSquareOffOnPartialFill)
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
            // have quantity outstanding - fully rejected/cancelled OR only partially filled (NOT
            // the hedge's deliberately-skipped partner - see RetryUnfilledLegsAsync), each
            // re-priced fresh off the current market, for just the outstanding quantity, at the
            // same wider slippage band, before giving up on the remainder and falling back to
            // unwinding whatever else already filled. Mutates legs/placements in place, so
            // legResults below and the square-off after it both see the final cumulative outcome
            // rather than the original rejection.
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

        if (_options.AutoSquareOffOnPartialFill && legPlans.Count > 1)
            legResults.AddRange(await SquareOffPartialFillAsync(rule, legPlans, placements, stoppingToken));

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

        // 2. Short (SELL) legs only once every hedge leg is confirmed filled (OrderResult.Success
        //    is true only on a real fill - see OrderResult). No buy legs at all (a sell-only rule)
        //    trivially passes: there's no hedge to wait for.
        var hedgeInPlace = buyIndices.Count == 0 || buyIndices.All(i => results[i].Success);
        if (hedgeInPlace)
        {
            var sellResults = await Task.WhenAll(sellIndices.Select(i => _orderGateway.PlaceOrderAsync(legs[i], cancellationToken)));
            for (var k = 0; k < sellIndices.Count; k++)
                results[sellIndices[k]] = sellResults[k];
        }
        else
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
        }

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

    /// <summary>Checks the basket's combined margin requirement (with the near/far hedge
    /// benefit applied - see IMarginService.GetBasketMarginAsync) against the account's
    /// actual available margin. Returns null if dispatch should proceed, or a human-readable
    /// reason if it should be blocked entirely (published to the dashboard via
    /// PublishBlockedAsync). Only called at all when EngineOptions.VerifyMarginBeforeDispatch is
    /// true - see DispatchAsync, which skips this (and the two Kite API round trips it makes)
    /// entirely when that's off. Blocks only when the account genuinely can't cover the basket
    /// plus EngineOptions.MarginSafetyBufferPercent headroom; either way the basket margin
    /// figures are logged for visibility. A failed *check* (network error, Kite API hiccup) is
    /// not treated as fatal - Kite's own order entry will reject a genuinely under-margined
    /// order regardless, so we'd rather attempt the basket than block it on a flaky diagnostic
    /// call. A confirmed *insufficient* margin, on the other hand, blocks dispatch entirely,
    /// since letting one leg place and the other get rejected for lack of margin is exactly the
    /// naked-position scenario this whole basket approach exists to avoid.</summary>
    private async Task<string?> CheckMarginAsync(string ruleId, List<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        var margin = await _marginService.GetBasketMarginAsync(legs, cancellationToken);
        if (!margin.Success)
        {
            _logger.LogWarning("Basket margin check failed for rule {RuleId}: {Error} - proceeding anyway", ruleId, margin.ErrorMessage);
            return null;
        }

        _logger.LogInformation(
            "Basket margin for rule {RuleId}: final={Final} (would be {Initial} without the spread hedge benefit)",
            ruleId, margin.FinalTotal, margin.InitialTotal);

        if (margin.FinalTotal is not { } required)
            return null;

        // NFO (equity F&O) settles against the "equity" margin segment; MCX commodities against
        // "commodity". Pick per-basket from the legs' own exchange so an MCX basket's pre-trade
        // check reads the right pool of available funds. With the MCX add-on off no basket ever
        // carries an MCX leg, so this always resolves to the original "equity" default.
        var marginSegment = _mcx.Enabled && legs.Any(l => string.Equals(l.Exchange, _mcx.Exchange, StringComparison.OrdinalIgnoreCase))
            ? _mcx.MarginSegment
            : "equity";
        var available = await _marginService.GetAvailableMarginAsync(marginSegment, cancellationToken);
        if (!available.Success || available.Net is not { } net)
        {
            _logger.LogWarning("Available margin check failed for rule {RuleId}: {Error} - proceeding anyway", ruleId, available.ErrorMessage);
            return null;
        }

        var buffered = required * (1 + _options.MarginSafetyBufferPercent / 100m);
        if (net < buffered)
        {
            var reason = $"Insufficient margin: needs {required:F2} (+{_options.MarginSafetyBufferPercent}% safety buffer = {buffered:F2}), only {net:F2} available.";
            _logger.LogError(
                "Rule {RuleId} basket NOT placed - {Reason} Placing only some legs would leave a naked, unhedged position, so nothing was sent.",
                ruleId, reason);
            return reason;
        }

        _logger.LogInformation("Margin check passed for rule {RuleId}: {Available:F2} available covers {Required:F2} + buffer.", ruleId, net, required);
        return null;
    }

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
    /// this gives up on the remainder and lets the caller's square-off fallback take over -
    /// NOTE that fallback only unwinds a leg with literally zero total fill; it does not
    /// currently detect/correct a case where retries end with the two legs at genuinely different
    /// filled quantities (e.g. leg A ends at 2 lots, leg B at 1) - that residual quantity mismatch
    /// is left as-is today. Mutates <paramref name="legs"/>/<paramref name="placements"/> in place
    /// at the retried index/indices so every caller downstream (legResults,
    /// SquareOffPartialFillAsync) sees the final cumulative outcome instead of the original
    /// rejection.</summary>
    private async Task RetryUnfilledLegsAsync(TriggerRule rule, List<LegPlan> legPlans, List<OrderLegRequest> legs, OrderResult[] placements, CancellationToken cancellationToken)
    {
        var filledCount = 0;
        var pending = new List<int>();
        for (var i = 0; i < placements.Length; i++)
        {
            if (placements[i].FilledQuantity > 0) filledCount++;
            if (placements[i].FilledQuantity >= legPlans[i].Request.Quantity) continue; // already fully filled - nothing outstanding
            if (string.Equals(placements[i].Status, "SKIPPED", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(placements[i].Status, "OPEN", StringComparison.OrdinalIgnoreCase)) continue;
            pending.Add(i);
        }

        if (filledCount == 0 || pending.Count == 0)
            return; // nothing filled yet to protect, or nothing eligible to retry

        var widerSlippage = rule.SlippagePercent * _options.RetryUnfilledLegSlippageMultiplier;
        var maxAttempts = Math.Max(1, _options.RetryUnfilledLegMaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
        {
            var nextRound = new List<int>();

            foreach (var i in pending)
            {
                var plan = legPlans[i];
                var already = placements[i];
                var outstandingQty = plan.Request.Quantity - already.FilledQuantity;

                _logger.LogWarning(
                    "Rule {RuleId}: {Symbol} {Side} has {Outstanding}/{Requested} lot(s) outstanding at {OriginalSlippage}% slippage ({Status}) - retry {Attempt}/{MaxAttempts} at {WiderSlippage}%.",
                    rule.Id, plan.Request.TradingSymbol, plan.Request.TransactionType, outstandingQty, plan.Request.Quantity, rule.SlippagePercent, already.Status, attempt, maxAttempts, widerSlippage);

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

                if (combinedFilled >= plan.Request.Quantity)
                {
                    _logger.LogInformation(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} completed {Symbol} {Side} - {Filled}/{Requested} lot(s) now filled at {WiderSlippage}% slippage -> order {OrderId}",
                        rule.Id, attempt, maxAttempts, plan.Request.TradingSymbol, plan.Request.TransactionType, combinedFilled, plan.Request.Quantity, widerSlippage, retryResult.OrderId);
                }
                else if (string.Equals(retryResult.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} for {Symbol} {Side} came back OPEN/unconfirmed - leaving it as-is rather than risking a further retry double-placing it.",
                        rule.Id, attempt, maxAttempts, plan.Request.TradingSymbol, plan.Request.TransactionType);
                }
                else
                {
                    var giveUp = attempt == maxAttempts;
                    _logger.LogWarning(
                        "Rule {RuleId}: retry {Attempt}/{MaxAttempts} at {WiderSlippage}% still leaves {Symbol} {Side} at {Filled}/{Requested} lot(s) - {Error}.{Next}",
                        rule.Id, attempt, maxAttempts, widerSlippage, plan.Request.TradingSymbol, plan.Request.TransactionType, combinedFilled, plan.Request.Quantity, retryResult.ErrorMessage,
                        giveUp ? " Giving up on the remainder - falling back to square-off." : " Trying again for the remainder.");
                    if (!giveUp)
                        nextRound.Add(i);
                }
            }

            pending = nextRound;
        }
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

    /// <summary>Flattens whichever leg(s) actually filled if the basket only partially went
    /// through, since a lone near or far month future is a completely different (and far more
    /// margin-expensive) risk than the intended hedged spread - see EngineOptions.
    /// AutoSquareOffOnPartialFill. Fires an opposite-direction order for each filled leg
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
        // Paired with the actually-confirmed filled quantity (OrderResult.FilledQuantity, now
        // that PlaceOrderAsync verifies a real fill instead of just placement acceptance) -
        // flattening must match what's genuinely in the position, not the originally requested
        // quantity, in case only part of a leg filled before Kite cancelled the rest (IOC).
        var filled = new List<(LegPlan Plan, int FilledQuantity)>();
        var anyRejected = false;
        for (var i = 0; i < legPlans.Count; i++)
        {
            if (results[i].Success && results[i].FilledQuantity > 0)
                filled.Add((legPlans[i], results[i].FilledQuantity));
            else
                anyRejected = true;
        }

        // Either everything filled (the hedge is intact, nothing to unwind) or nothing filled
        // (nothing to unwind either) - only a genuine partial fill needs cleanup.
        if (!anyRejected || filled.Count == 0) return squareOffResults;

        _logger.LogError(
            "Rule {RuleId} basket PARTIALLY FILLED - {FilledCount}/{TotalCount} leg(s) actually filled, leaving a naked, unhedged position. Auto-squaring off the filled leg(s) now.",
            ruleId, filled.Count, legPlans.Count);

        foreach (var (plan, filledQuantity) in filled)
        {
            var leg = plan.Request;
            var flattenLeg = new OrderLegRequest
            {
                TradingSymbol = leg.TradingSymbol,
                Exchange = leg.Exchange,
                InstrumentToken = leg.InstrumentToken,
                TransactionType = leg.TransactionType == "BUY" ? "SELL" : "BUY",
                Quantity = filledQuantity, // flatten exactly what filled, not the original requested quantity
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
