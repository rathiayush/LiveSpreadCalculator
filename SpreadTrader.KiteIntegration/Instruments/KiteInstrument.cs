namespace SpreadTrader.KiteIntegration.Instruments;

public sealed record KiteInstrument(
    uint InstrumentToken,
    string TradingSymbol,
    string Name,
    string Exchange,
    string Segment,
    string InstrumentType,
    DateOnly? Expiry,
    int LotSize = 0);
