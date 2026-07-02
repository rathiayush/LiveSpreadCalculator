namespace SpreadTrader.Core.Models;

/// <summary>
/// A single market data snapshot for one instrument, as received from the Kite ticker.
/// Kept as a struct so the hot ingestion path avoids per-tick heap allocations.
/// </summary>
public readonly record struct Tick(
    uint InstrumentToken,
    decimal LastPrice,
    long LastTradedQuantity,
    long Volume,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long OpenInterest,
    DateTimeOffset ExchangeTimestamp,
    DateTimeOffset ReceivedAt,
    decimal BestBidPrice = 0,
    decimal BestAskPrice = 0)
{
    /// <summary>True once market depth (best bid/ask) has actually been received for this
    /// instrument - only populated in Kite's "full" ticker mode, not "ltp"/"quote".</summary>
    public bool HasDepth => BestBidPrice > 0 || BestAskPrice > 0;
}
