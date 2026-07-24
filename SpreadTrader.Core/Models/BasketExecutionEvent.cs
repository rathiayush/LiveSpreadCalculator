namespace SpreadTrader.Core.Models;

/// <summary>Outcome of placing one order - either an original basket leg or a compensating
/// auto-square-off order fired afterward because the basket only partially filled (IsSquareOff
/// distinguishes the two) - see OrderDispatchHostedService and BasketExecutionEvent. Quantity is
/// what was requested; FilledQuantity is what Kite actually confirmed filled (see OrderResult) -
/// they can differ even when Success is true (a genuine partial fill within this one leg).
///
/// Price is the price the order was actually sent at - for a MARKET leg converted to a protected
/// LIMIT (see OrderDispatchHostedService.ApplyMarketProtection) that's the protection-band limit
/// price; null means it went as a raw MARKET order with no price reference. PriceReference says
/// which live price that protection band was anchored to - "Bid/Ask" or "LTP" (see
/// OrderLegRequest.PriceReference/MarketProtectionService) - null alongside a null Price. OrderType/
/// Status carry the sent order type and Kite's raw terminal status (COMPLETE/REJECTED/CANCELLED/
/// OPEN...) so the Telegram/dashboard readout can show exactly what was placed, at what price, and
/// off which reference.</summary>
public sealed record OrderLegResult(
    string TradingSymbol,
    string TransactionType, // BUY or SELL
    int Quantity,
    bool Success,
    string? OrderId,
    string? ErrorMessage,
    bool IsSquareOff = false,
    int FilledQuantity = 0,
    decimal? Price = null,
    string? OrderType = null,
    string? Status = null,
    string? PriceReference = null,
    /// <summary>Kite's own fill-weighted average price for this order (see
    /// OrderResult.AverageFillPrice) - what this leg ACTUALLY executed at, as opposed to Price
    /// above (what it was SENT at). Null until a fill is confirmed. This is the real "at what price
    /// did our position get cut" figure - Price is only an approximation via the protection band.</summary>
    decimal? AverageFillPrice = null);

/// <summary>Published once OrderDispatchHostedService finishes acting on a fired trigger -
/// correlates back to the TriggerEvent that caused it via FireId, and reports what was actually
/// bought/sold, whether each leg succeeded, and the rejection reason for any that didn't - so
/// the dashboard can show real execution outcomes instead of just "a trigger fired". This is
/// always populated (Underlying/OrderKind), whether the basket succeeded, partially failed, or
/// was blocked entirely, so the feed never shows a fire with no context about what it was for.
/// If the pre-trade margin check blocked the basket entirely (see EngineOptions.
/// VerifyMarginBeforeDispatch), Legs is empty, MarginBlocked is true, and BlockedReason explains
/// why - nothing was placed with Kite at all in that case.</summary>
public sealed record BasketExecutionEvent(
    Guid FireId,
    string RuleId,
    string SpreadId,
    string Underlying,
    /// <summary>"Basket" (2+ legs attempted), "Single leg" (1 leg attempted), "None" (rule fired
    /// but resolved to zero leg actions), or "Unknown" (rule/spread was deleted before dispatch
    /// could even figure out what to place) - see OrderDispatchHostedService.DescribeOrderKind.</summary>
    string OrderKind,
    IReadOnlyList<OrderLegResult> Legs,
    bool MarginBlocked,
    string? BlockedReason,
    DateTimeOffset ExecutedAt,
    /// <summary>The spread value that actually crossed the rule's threshold and caused this fire -
    /// copied straight from TriggerEvent.SpreadValueAtTrigger at dispatch time. Null for a
    /// BasketExecutionEvent raised outside of a trigger fire (currently only PositionsController's
    /// manual square-off, which has no TriggerEvent to draw this from). Lets the Telegram
    /// notification (and any future consumer) show "what level fired this" alongside "what the legs
    /// actually filled at" - see TelegramOrderExecutionSink.BuildMessage.</summary>
    decimal? SpreadValueAtTrigger = null);
