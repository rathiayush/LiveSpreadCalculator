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
    decimal BestAskPrice = 0,
    /// <summary>The canonical exchange ("NFO"/"NSE"/"IDX"/... - see BrokerInstrument.Exchange) this
    /// tick's InstrumentToken was received under, or null when the source ticker doesn't carry one
    /// (Kite's instrument_token is globally unique, so KiteTickerClient never needs to set this).
    ///
    /// EXISTS BECAUSE DHAN'S securityId IS ONLY UNIQUE WITHIN A SEGMENT - see DhanSegmentRegistry's
    /// doc comment. Confirmed against a real scrip master download: securityId 13 is BOTH ABB
    /// India's equity (NSE_EQ) AND the NIFTY 50 index (IDX_I). InstrumentToken alone therefore
    /// cannot safely identify an instrument on Dhan; a consumer that cares about that distinction
    /// (see CdvHostedService.OnTick) must check this field before trusting a tick belongs to the
    /// instrument it thinks it subscribed. ITickStore itself is NOT segment-aware and stays keyed by
    /// the bare token - this field lets an individual consumer defend itself without that broader,
    /// riskier change.</summary>
    string? Segment = null)
{
    /// <summary>True once market depth (best bid/ask) has actually been received for this
    /// instrument - only populated in Kite's "full" ticker mode, not "ltp"/"quote".</summary>
    public bool HasDepth => BestBidPrice > 0 || BestAskPrice > 0;
}
