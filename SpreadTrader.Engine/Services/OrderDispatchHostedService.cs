using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine;
using SpreadTrader.KiteIntegration.Orders;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Consumes fired triggers and turns each into a basket of order legs. Runs as its own consumer
/// loop off a channel so a slow or failing order call never blocks tick/spread processing
/// upstream.
///
/// "Basket" here means two things:
///  1. Before placing anything, the basket's combined margin requirement is checked against
///     Kite's basket-margin API (which applies the hedge/spread benefit between offsetting near
///     and far legs on the same underlying - see KiteMarginService.GetBasketMarginAsync) and,
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
///     ApplyMarketProtection) - IOC fills what it can immediately and cancels the remainder
///     right away instead of resting, so its outcome is known almost instantly rather than
///     staying ambiguous for a long time.
///  c. KiteOrderGateway.PlaceOrderAsync polls the order's real status after placement instead of
///     trusting mere placement-acceptance as "success" - if a leg is confirmed filled while
///     another wasn't, the filled one is immediately flattened (SquareOffPartialFillAsync); if a
///     leg's status couldn't be confirmed as terminal in time, it's actively cancelled rather
///     than left resting and unmanaged (CancelStrayOpenOrdersAsync).
///
/// Any leg configured (or, for square-off, hardcoded) as OrderType=MARKET is converted to a
/// protected LIMIT order before it's sent - see ApplyMarketProtection. Kite Connect's API
/// rejects plain MARKET orders for F&amp;O outright ("Market orders without market protection are
/// not allowed via API"), and there's no market_protection parameter on the order-placement
/// endpoint itself to opt back in - a bounded-slippage LIMIT order is the supported workaround.
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
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly ITickStore _tickStore;
    private readonly IOrderGateway _orderGateway;
    private readonly KiteMarginService _marginService;
    private readonly IOrderExecutionSink _executionSink;
    private readonly EngineOptions _options;
    private readonly ILogger<OrderDispatchHostedService> _logger;

    public OrderDispatchHostedService(
        TriggerEventChannel triggerChannel,
        ITriggerRuleStore ruleStore,
        ISpreadDefinitionStore spreadStore,
        ITickStore tickStore,
        IOrderGateway orderGateway,
        KiteMarginService marginService,
        IOrderExecutionSink executionSink,
        IOptions<EngineOptions> options,
        ILogger<OrderDispatchHostedService> logger)
    {
        _triggerChannel = triggerChannel;
        _ruleStore = ruleStore;
        _spreadStore = spreadStore;
        _tickStore = tickStore;
        _orderGateway = orderGateway;
        _marginService = marginService;
        _executionSink = executionSink;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var triggerEvent in _triggerChannel.Reader.ReadAllAsync(stoppingToken))
        {
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
        }
    }

    private async Task DispatchAsync(TriggerEvent triggerEvent, CancellationToken stoppingToken)
    {
        var rule = _ruleStore.Get(triggerEvent.RuleId);
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

        var legPlans = BuildLegs(rule, spread, triggerEvent.Direction);
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
        var legs = legPlans.Select(plan => ApplyMarketProtection(plan, rule.Id)).ToList();

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
                return; // fail-safe: nothing placed at all
            }
        }

        // Place the hedge (BUY) leg(s) first, confirm they fill, and only then place the short
        // (SELL) leg(s) - see PlaceHedgeFirstAsync and the class doc comment for why. This is what
        // gets the exchange's hedge margin benefit applied to the short instead of it being blocked
        // as a naked short, and keeps the brief between-legs window on the safe (long) side.
        var placements = await PlaceHedgeFirstAsync(legs, rule.Id, stoppingToken);

        var legResults = new List<OrderLegResult>(legs.Count);
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var result = placements[i];
            legResults.Add(new OrderLegResult(leg.TradingSymbol, leg.TransactionType, leg.Quantity, result.Success, result.OrderId, result.ErrorMessage, FilledQuantity: result.FilledQuantity, Price: leg.Price, OrderType: leg.OrderType, Status: result.Status));

            if (!result.Success)
            {
                _logger.LogError("Basket leg failed for rule {RuleId}: {Symbol} {Side} - {Error}",
                    rule.Id, leg.TradingSymbol, leg.TransactionType, result.ErrorMessage);
            }
        }

        if (_options.AutoSquareOffOnPartialFill)
        {
            // A leg whose fill status came back "OPEN" (the post-placement poll gave up before
            // Kite reached a terminal status - see KiteOrderGateway.PollOrderStatusAsync) is
            // still live on the exchange and could fill later on its own, creating an untracked
            // position with no square-off ever fired for it. Cancel those outright rather than
            // leaving them to resolve unattended - this only ever affects DAY-validity legs
            // (protected IOC legs resolve to a terminal status almost immediately, so they
            // essentially never land here).
            await CancelStrayOpenOrdersAsync(rule.Id, legs, placements, stoppingToken);

            if (legPlans.Count > 1)
                legResults.AddRange(await SquareOffPartialFillAsync(rule.Id, legPlans, placements, stoppingToken));
        }

        await _executionSink.PublishAsync(
            new BasketExecutionEvent(triggerEvent.FireId, rule.Id, spread.Id, spread.Underlying, orderKind, legResults, MarginBlocked: false, BlockedReason: null, DateTimeOffset.UtcNow),
            stoppingToken);
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
            new BasketExecutionEvent(triggerEvent.FireId, triggerEvent.RuleId, triggerEvent.SpreadId, underlying, orderKind, Array.Empty<OrderLegResult>(), MarginBlocked: true, BlockedReason: reason, DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Fallback tick size used when an instrument's real one hasn't been resolved yet
    /// (universe built before TickSize was tracked, or InstrumentMasterService hasn't loaded) -
    /// 0.05 is the tick size for the overwhelming majority of NSE F&amp;O contracts, so it's a far
    /// better guess than skipping alignment entirely.</summary>
    private const decimal DefaultTickSize = 0.05m;

    /// <summary>Kite Connect rejects a plain OrderType=MARKET leg for F&amp;O with "Market orders
    /// without market protection are not allowed via API" - there's no market_protection
    /// parameter on the order-placement endpoint to opt back in, so any MARKET leg is converted
    /// here to a LIMIT order priced EngineOptions.MarketProtectionPercent through the best
    /// available quote: best-ask * (1 + pct/100) for a BUY (willing to pay up to that much more
    /// to all-but-guarantee a fill), best-bid * (1 - pct/100) for a SELL. Falls back to the last
    /// traded price if full-mode depth hasn't arrived yet for that instrument, and leaves the
    /// leg as MARKET (unchanged) only if there's no price reference at all yet - in which case
    /// Kite will still reject it, but there's nothing better to send.
    ///
    /// The raw band-adjusted price is then aligned to the instrument's actual tick size (Kite
    /// rejects a LIMIT price that isn't an exact multiple of it, e.g. "Tick size for this script
    /// is 0.05" - a flat 2-decimal round doesn't guarantee that) via RoundToTick: rounded up for
    /// a BUY and down for a SELL, so the sent price is never more restrictive than the intended
    /// protection band - see RoundToTick's own comment for why the rounding direction matters.
    /// </summary>
    private OrderLegRequest ApplyMarketProtection(LegPlan plan, string ruleId)
    {
        var leg = plan.Request;
        if (!string.Equals(leg.OrderType, "MARKET", StringComparison.OrdinalIgnoreCase))
            return leg;

        if (!_tickStore.TryGet(plan.InstrumentToken, out var tick))
        {
            _logger.LogWarning(
                "Rule {RuleId}: no tick data yet for {Symbol} - sending as MARKET anyway, Kite will likely reject it (market protection requires a price reference).",
                ruleId, leg.TradingSymbol);
            return leg;
        }

        var isBuy = string.Equals(leg.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase);
        var reference = isBuy
            ? (tick.BestAskPrice > 0 ? tick.BestAskPrice : tick.LastPrice)
            : (tick.BestBidPrice > 0 ? tick.BestBidPrice : tick.LastPrice);

        if (reference <= 0)
        {
            _logger.LogWarning("Rule {RuleId}: no usable price reference for {Symbol} yet - sending as MARKET anyway.", ruleId, leg.TradingSymbol);
            return leg;
        }

        var band = _options.MarketProtectionPercent / 100m;
        var rawPrice = isBuy ? reference * (1 + band) : reference * (1 - band);

        var tickSize = plan.TickSize > 0 ? plan.TickSize : DefaultTickSize;
        if (plan.TickSize <= 0)
        {
            _logger.LogWarning(
                "Rule {RuleId}: no tick size resolved for {Symbol} - falling back to {Default} (rebuild the universe to pick up real tick sizes).",
                ruleId, leg.TradingSymbol, DefaultTickSize);
        }

        var protectedPrice = RoundToTick(rawPrice, tickSize, roundUp: isBuy);

        _logger.LogInformation(
            "Rule {RuleId}: {Symbol} MARKET -> protected LIMIT @ {Price} ({Percent}% band around {Reference}, tick size {TickSize})",
            ruleId, leg.TradingSymbol, protectedPrice, _options.MarketProtectionPercent, reference, tickSize);

        return new OrderLegRequest
        {
            TradingSymbol = leg.TradingSymbol,
            Exchange = leg.Exchange,
            TransactionType = leg.TransactionType,
            Quantity = leg.Quantity,
            Product = leg.Product,
            OrderType = "LIMIT",
            Price = protectedPrice,
            // IOC instead of the DAY default: fill whatever it can immediately and cancel the
            // rest right away, rather than resting in the book if price moves past the
            // protection band. A resting DAY order at this price could otherwise sit open and
            // unfilled for a long time with no fast way to detect it - IOC turns that into an
            // immediate, known outcome that KiteOrderGateway's post-placement status poll can
            // actually observe within a second or two, which is what makes
            // AutoSquareOffOnPartialFill trustworthy. This is also the closest thing Kite offers
            // to "fill or don't" for an individual leg - there's no true FOK/all-or-none order
            // type on this exchange, for a single leg or for a basket (see the class doc
            // comment and ARCHITECTURE.md).
            Validity = "IOC",
            Variety = leg.Variety,
            Tag = leg.Tag,
        };
    }

    /// <summary>Aligns a price to the nearest valid multiple of the instrument's tick size, in
    /// whichever direction keeps the order at least as permissive as the raw band-adjusted price
    /// - never more restrictive, since that could shave a fraction of a tick off the intended
    /// slippage protection and occasionally cost a fill for no real benefit. For a BUY, rounds up
    /// (willing to pay at least the intended protected price, never less); for a SELL, rounds
    /// down (willing to accept at least as low, never more). Kite LIMIT prices are always at most
    /// 2 decimal places regardless of tick size (0.05, 0.10, 1.00, ...), so the result is rounded
    /// to 2 decimals afterwards as a final safety net against any residual fractional-paisa
    /// noise from the multiplication/division.</summary>
    private static decimal RoundToTick(decimal price, decimal tickSize, bool roundUp)
    {
        if (tickSize <= 0) return Math.Round(price, 2);
        var ticks = price / tickSize;
        var roundedTicks = roundUp ? Math.Ceiling(ticks) : Math.Floor(ticks);
        return Math.Round(roundedTicks * tickSize, 2);
    }

    /// <summary>Checks the basket's combined margin requirement (with the near/far hedge
    /// benefit applied - see KiteMarginService.GetBasketMarginAsync) against the account's
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

        var available = await _marginService.GetAvailableMarginAsync(cancellationToken: cancellationToken);
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

            await _orderGateway.CancelOrderAsync(result.OrderId, legs[i].Variety, cancellationToken);
        }
    }

    /// <summary>Flattens whichever leg(s) actually filled if the basket only partially went
    /// through, since a lone near or far month future is a completely different (and far more
    /// margin-expensive) risk than the intended hedged spread - see EngineOptions.
    /// AutoSquareOffOnPartialFill. Fires an opposite-direction order for each filled leg
    /// immediately (also passed through ApplyMarketProtection, same as any other MARKET leg -
    /// this is cleanup, not a rate-sensitive trade, but Kite still rejects a raw MARKET order
    /// here too); if even the square-off fails, that's logged as Critical since it means the
    /// account is left with a naked position needing manual attention. Returns the square-off
    /// attempts (IsSquareOff=true) so they're included in the published BasketExecutionEvent
    /// alongside the original legs.</summary>
    private async Task<List<OrderLegResult>> SquareOffPartialFillAsync(string ruleId, List<LegPlan> legPlans, OrderResult[] results, CancellationToken cancellationToken)
    {
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
            var flattenPlan = new LegPlan(
                new OrderLegRequest
                {
                    TradingSymbol = leg.TradingSymbol,
                    Exchange = leg.Exchange,
                    TransactionType = leg.TransactionType == "BUY" ? "SELL" : "BUY",
                    Quantity = filledQuantity, // flatten exactly what filled, not the original requested quantity
                    Product = leg.Product,
                    OrderType = "MARKET", // this is risk cleanup, not a rate-sensitive trade - ApplyMarketProtection still converts it to a protected LIMIT before it's sent
                    Tag = leg.Tag,
                },
                plan.InstrumentToken,
                plan.TickSize);

            var flatten = ApplyMarketProtection(flattenPlan, ruleId);
            var result = await _orderGateway.PlaceOrderAsync(flatten, cancellationToken);
            squareOffResults.Add(new OrderLegResult(flatten.TradingSymbol, flatten.TransactionType, flatten.Quantity, result.Success, result.OrderId, result.ErrorMessage, IsSquareOff: true, FilledQuantity: result.FilledQuantity, Price: flatten.Price, OrderType: flatten.OrderType, Status: result.Status));

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
    /// size - BuildLegs knows both (spread.NearLeg/FarLeg.InstrumentToken / .TickSize) but
    /// OrderLegRequest itself has neither field (Kite's order API only wants tradingsymbol+
    /// exchange+price), so they're carried alongside rather than looked up later - see
    /// ApplyMarketProtection.</summary>
    private readonly record struct LegPlan(OrderLegRequest Request, uint InstrumentToken, decimal TickSize);

    private static List<LegPlan> BuildLegs(TriggerRule rule, SpreadDefinition spread, string direction)
    {
        var (nearAction, farAction) = rule.AutoDirectionFromSpreadSign
            ? ResolveAutoDirection(direction)
            : (rule.NearLegAction, rule.FarLegAction);

        var legs = new List<LegPlan>();
        var tag = BuildTag(rule.Id);

        if (nearAction != TriggerLegAction.None)
        {
            legs.Add(new LegPlan(
                new OrderLegRequest
                {
                    TradingSymbol = spread.NearLeg.TradingSymbol,
                    Exchange = spread.NearLeg.Exchange,
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

    /// <summary>
    /// The calendar-spread arbitrage rule, decided by SpreadCalculationHostedService and carried
    /// here verbatim via TriggerEvent.Direction (never re-derived from SpreadValueAtTrigger's
    /// sign - see the comment on TriggerEvent for why that would be unsafe): if the current
    /// (near) month is in premium, sell it and buy the next (far) month; if it's at a discount,
    /// buy it and sell the far month. "Flat" (or a direction string this switch doesn't
    /// recognize) is treated as "nothing to do" since there's no direction to take a position in.
    /// </summary>
    private static (TriggerLegAction Near, TriggerLegAction Far) ResolveAutoDirection(string direction) =>
        direction switch
        {
            "BuyNear/SellFar" or "BuyNear/SellFar (bid pending)" => (TriggerLegAction.Buy, TriggerLegAction.Sell),
            "SellNear/BuyFar" or "SellNear/BuyFar (bid pending)" => (TriggerLegAction.Sell, TriggerLegAction.Buy),
            _ => (TriggerLegAction.None, TriggerLegAction.None),
        };

    private static string BuildTag(string ruleId)
    {
        // Kite tags must be alphanumeric and <= 20 chars.
        var raw = "str" + new string(ruleId.Where(char.IsLetterOrDigit).ToArray());
        return raw.Length > 20 ? raw[..20] : raw;
    }
}
