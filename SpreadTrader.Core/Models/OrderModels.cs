namespace SpreadTrader.Core.Models;

public sealed class OrderLegRequest
{
    public required string TradingSymbol { get; set; }
    public required string Exchange { get; set; }
    public required string TransactionType { get; set; } // BUY or SELL
    public required int Quantity { get; set; }
    public string Product { get; set; } = "NRML";
    public string OrderType { get; set; } = "MARKET";
    public decimal? Price { get; set; } // required for LIMIT orders

    /// <summary>Which live price MarketProtectionService anchored the protection band to when it
    /// converted this leg from MARKET to a protected LIMIT - "Bid/Ask" (best ask for a BUY, best
    /// bid for a SELL, from the live ticker) or "LTP" (last traded price, used either as a
    /// fallback when depth hasn't arrived yet, or as the only reference available at all for
    /// manual square-off - see MarketProtectionService.Protect/ProtectFromReference). Null for a
    /// leg that was never MARKET-protected (a plain LIMIT order the caller priced itself).</summary>
    public string? PriceReference { get; set; }

    /// <summary>DAY (rests in the book until it fills or the session ends) or IOC (fills
    /// whatever quantity it can immediately, cancels the rest right away instead of resting).
    /// OrderDispatchHostedService.ApplyMarketProtection sets this to IOC on every protected
    /// LIMIT order it produces - a DAY order at a protection-band price could otherwise sit
    /// open and unfilled for a long time if price moves away, leaving a basket leg in a
    /// silent, long-lived "placed but not actually filled" state that AutoSquareOffOnPartialFill
    /// has no fast way to detect. IOC turns that into an immediate, known outcome.</summary>
    public string Validity { get; set; } = "DAY";

    public string Variety { get; set; } = "regular";
    public string? Tag { get; set; }
}

public sealed class OrderResult
{
    /// <summary>True only if the order actually resulted in a fill (FilledQuantity &gt; 0) -
    /// not merely that Kite accepted it for processing. An IOC order that got fully cancelled
    /// with zero fill, or one whose terminal status couldn't be confirmed in time, is Success =
    /// false here even though Kite returned an order_id for it - see KiteOrderGateway.
    /// PlaceOrderAsync, which polls the order's status after placement specifically so this
    /// distinction is meaningful (previously this only reflected placement acceptance, which
    /// made a resting, still-unfilled order look identical to a genuinely filled one).</summary>
    public bool Success { get; set; }

    public string? OrderId { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>How much of Quantity actually got filled, per Kite's own order status (0 if
    /// rejected/cancelled/still pending when the status poll gave up). Can be less than the
    /// requested quantity even when Success is true (a genuine partial fill within one leg) -
    /// callers that square off a filled leg should flatten this amount, not the original
    /// requested quantity.</summary>
    public int FilledQuantity { get; set; }

    /// <summary>Kite's raw order status string (COMPLETE, REJECTED, CANCELLED, OPEN, ...) at
    /// the time the status poll stopped - OPEN here means the poll gave up before Kite reached
    /// a terminal state, not that the order is confirmed still resting.</summary>
    public string? Status { get; set; }

    /// <summary>Kite's own fill-weighted average price for this order (the "average_price" field
    /// on the order status history - see KiteOrderGateway.PollOrderStatusAsync), i.e. what this leg
    /// ACTUALLY executed at, as opposed to OrderLegRequest.Price (what it was SENT at - the
    /// protection-band limit price for a MARKET-converted leg). Null until a fill is confirmed
    /// (Kite reports 0 for an order with no fill yet, normalized to null here) - used to compute
    /// the real cut/fill spread rather than an approximation from the sent price.</summary>
    public decimal? AverageFillPrice { get; set; }
}
