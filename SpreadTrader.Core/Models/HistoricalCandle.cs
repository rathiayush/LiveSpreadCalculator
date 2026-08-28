namespace SpreadTrader.Core.Models;

/// <summary>One OHLCV bar from a broker's historical/intraday candle API - see
/// IHistoricalCandleSource. Used only to reconstruct a previous trading day's CDV curve
/// (CdvHostedService), since historical candles carry no real trade-by-trade buy/sell tagging -
/// same limitation the Pine Script indicator this feature ports has for ITS bar-proxy formula.</summary>
public readonly record struct HistoricalCandle(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
