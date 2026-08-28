namespace SpreadTrader.Core.Models;

/// <summary>
/// One bar of the CDV (cumulative delta volume) time series for one underlying's option chain -
/// the CE and PE totals as of that bar's close. Sampled on a fixed timer by CdvHostedService (see
/// CdvOptions.TimeframeSeconds), exactly the role a TradingView chart's OHLC bars play for the
/// Pine "delta auto" indicator this feature ports: lookback-N-bars and EMA-over-bars both index
/// into a list of these, the same way Pine indexes ceActive[lookbackBars] / ta.ema(...) against its
/// own chart's bar series. CeCdv/PeCdv are already "since today's open" - the accumulator itself is
/// reset to zero at the first tick of each IST trading day (see CdvHostedService), so unlike the
/// Pine script there is no separate day-open baseline to subtract.
/// </summary>
public readonly record struct CdvBarPoint(DateTimeOffset TimestampUtc, decimal CeCdv, decimal PeCdv);

/// <summary>
/// One lookback window's CE/PE % change, as shown in the Pine script's "Multi-Lookback" table
/// (Chg ±20B / ±25B / ±30B). Bars is however many bars ago CeCdv/PeCdv is compared against
/// (CdvOptions.MultiLookbackBars); the percent is null until that many bars have actually been
/// captured today (mirrors Pine's `low bars ago -> na, not 0%`).
/// </summary>
public sealed record CdvLookbackChange(
    int Bars,
    decimal? CePercent,
    decimal? PePercent,
    bool CeHit,
    bool PeHit);

/// <summary>
/// The latest computed CDV state for one underlying's option chain, pushed to the dashboard and
/// served by GET /api/cdv/snapshot. Everything here is EMA-smoothed and bar-indexed the same way
/// the Pine script computes it (see CdvBarPoint) - CeCdv/PeCdv are the raw (unsmoothed) running
/// totals since today's open; CeEma/PeEma are ta.ema(CdvOptions.SmoothLen) over the bar series and
/// are what the lookback/frozen/threshold/cross comparisons below are actually computed against,
/// exactly mirroring the Pine script's ceActive/peActive (which is the smoothed series, not the raw
/// one - see displayMode/ceRawEma in the original indicator).
/// </summary>
public sealed record CdvSnapshot(
    string Underlying,
    DateTimeOffset TimestampUtc,
    int AtmStrike,
    int StrikeStep,
    DateOnly Expiry,
    decimal SpotPrice,
    decimal CeCdv,
    decimal PeCdv,
    decimal CeEma,
    decimal PeEma,
    int BarCount,
    /// <summary>% change of CeEma/PeEma vs CdvOptions.LookbackBars bars ago - null until that many
    /// bars exist today. Mirrors Pine's ceLookbackChangePct/peLookbackChangePct.</summary>
    decimal? CeLookbackPercent,
    decimal? PeLookbackPercent,
    bool CeLookbackHit,
    bool PeLookbackHit,
    /// <summary>The EMA value captured the first bar at/after CdvOptions.FrozenTimeIst today - null
    /// until that time has passed. Mirrors Pine's ceFrozenVal/peFrozenVal.</summary>
    decimal? CeFrozenValue,
    decimal? PeFrozenValue,
    decimal? CeFrozenPercent,
    decimal? PeFrozenPercent,
    bool CeFrozenHit,
    bool PeFrozenHit,
    /// <summary>One entry per CdvOptions.MultiLookbackBars value - the Pine script's horizontal
    /// multi-lookback table.</summary>
    IReadOnlyList<CdvLookbackChange> MultiLookback,
    /// <summary>True on the bar CeEma crosses above/below PeEma - mirrors Pine's crossUp/crossDown
    /// (ta.crossover/ta.crossunder on ceActive vs peActive).</summary>
    bool CrossUp,
    bool CrossDown,
    /// <summary>The previous trading day's closing (last-bar) CDV value, reconstructed from Dhan's
    /// historical intraday candles via Pine's own bar-proxy formula - see CdvHostedService.
    /// BackfillPreviousDayAsync. Null until that backfill completes (or if it found no data - a
    /// market holiday, for instance). Mirrors Pine's cePrevDayClose/pePrevDayClose.</summary>
    decimal? CePreviousDayClose,
    decimal? PePreviousDayClose,
    /// <summary>Today's CeEma/PeEma vs CePreviousDayClose/PePreviousDayClose - mirrors Pine's
    /// ceDayChangePct/peDayChangePct in the "Day vs Current" table. Null whenever the corresponding
    /// PreviousDayClose is null.</summary>
    decimal? CeDayChangePercent,
    decimal? PeDayChangePercent);
