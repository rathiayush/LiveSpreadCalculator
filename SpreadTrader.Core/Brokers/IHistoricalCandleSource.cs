using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Brokers;

/// <summary>
/// One instrument's intraday OHLCV candles for a single past trading day, used only by
/// CdvHostedService to reconstruct the previous day's CDV curve (via the Pine Script's own
/// bar-proxy formula - historical candles have no real trade-by-trade buy/sell tagging, the same
/// limitation Pine itself has). Never used for live/current-day data - see ISpotPriceBootstrap for
/// that.
///
/// Dhan-only in practice, its own narrow interface for the same "let SpreadTrader.Engine depend on
/// this without referencing a broker project" reasoning as IOptionChainSource/ISpotPriceBootstrap.
/// </summary>
public interface IHistoricalCandleSource
{
    /// <summary>Every candle for <paramref name="day"/> (that day's market-open-to-close session),
    /// ascending by time. Returns an empty list on any failure (not authenticated, network error,
    /// no data for that day - a market holiday, for instance) - callers treat that as "nothing to
    /// backfill", not as an error to surface.</summary>
    Task<IReadOnlyList<HistoricalCandle>> GetIntradayCandlesAsync(
        uint instrumentToken, string brokerSegment, DateOnly day, int intervalMinutes, CancellationToken cancellationToken);
}
