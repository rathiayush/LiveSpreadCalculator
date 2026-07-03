namespace SpreadTrader.KiteIntegration.Instruments;

public sealed record KiteInstrument(
    uint InstrumentToken,
    string TradingSymbol,
    string Name,
    string Exchange,
    string Segment,
    string InstrumentType,
    DateOnly? Expiry,
    int LotSize = 0,
    // Minimum price increment for this contract (Kite's tick_size CSV column) - order
    // prices must be an exact multiple of this or Kite rejects the order outright. Mostly
    // 0.05 for NSE F&O but genuinely varies by instrument, hence tracked per-contract rather
    // than assumed - see OrderDispatchHostedService.ApplyMarketProtection.
    decimal TickSize = 0);
