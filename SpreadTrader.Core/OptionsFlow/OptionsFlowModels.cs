namespace SpreadTrader.Core.OptionsFlow;

/// <summary>Which side of the option chain a contract sits on.</summary>
public enum OptionRight
{
    Call = 0,
    Put = 1,
}

/// <summary>
/// One resolved index-option contract in the tracked strike window. Deliberately separate from
/// BrokerInstrument: that type describes the futures universe the trading engine works in, and
/// this add-on must not widen it (see IOptionsInstrumentSource for why the option chain is held
/// apart from the futures index).
/// </summary>
/// <param name="InstrumentToken">The broker's numeric handle - Kite's instrument_token, Dhan's
/// securityId. This is the tick-store key and the market-feed subscription key.</param>
/// <param name="TradingSymbol">The broker's own symbol for the contract, for display and logs.</param>
/// <param name="Underlying">Index name, e.g. "NIFTY".</param>
/// <param name="Exchange">Canonical exchange code - "NFO" for both NSE index option chains.</param>
/// <param name="Segment">The broker's native segment string, needed by Dhan's subscribe payload
/// and its history endpoint.</param>
/// <param name="Strike">Strike price.</param>
/// <param name="Right">Call or put.</param>
/// <param name="Expiry">Contract expiry.</param>
/// <param name="LotSize">Exchange lot size (carried for completeness; the flow maths works in raw
/// traded volume, exactly as the reference Pine script does).</param>
public sealed record OptionContract(
    uint InstrumentToken,
    string TradingSymbol,
    string Underlying,
    string Exchange,
    string Segment,
    decimal Strike,
    OptionRight Right,
    DateOnly? Expiry,
    int LotSize = 0);

/// <summary>
/// One minute of OHLCV for a single contract. The unit both halves of this feature speak in:
/// the historical backfill returns these directly, and the live path builds them from ticks. Note
/// <paramref name="Volume"/> is the volume traded WITHIN this minute, not the day's running total -
/// the live path differences the broker's cumulative day volume to get it.
/// </summary>
public readonly record struct OptionBar(
    DateTimeOffset MinuteUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

/// <summary>One point of an aggregated CE or PE series - the value actually plotted.</summary>
public readonly record struct OptionFlowPoint(DateTimeOffset MinuteUtc, decimal Cdv, decimal Obv);

/// <summary>
/// Everything the dashboard needs to draw one index's Options Flow chart, as of one moment.
/// </summary>
/// <param name="Index">Index name.</param>
/// <param name="AtmStrike">The strike the window is currently centred on.</param>
/// <param name="Spot">Index level the ATM was derived from.</param>
/// <param name="Expiry">Expiry of the tracked chain.</param>
/// <param name="StrikeCount">How many contracts are in the window per side.</param>
/// <param name="WindowSetAtUtc">When the window was last (re-)centred - a shift rebuilds the whole
/// series from session start, so this doubles as "these numbers were computed over this window".</param>
/// <param name="DeltaWeighted">Whether delta weighting was applied.</param>
/// <param name="Backfilled">True when the series was seeded from broker history rather than only
/// from ticks observed since the process started - see IOptionHistoryProvider.</param>
/// <param name="Call">The CE series, oldest first.</param>
/// <param name="Put">The PE series, oldest first.</param>
public sealed record OptionsFlowSnapshot(
    string Index,
    decimal AtmStrike,
    decimal Spot,
    DateOnly? Expiry,
    int StrikeCount,
    DateTimeOffset WindowSetAtUtc,
    bool DeltaWeighted,
    bool Backfilled,
    IReadOnlyList<OptionFlowPoint> Call,
    IReadOnlyList<OptionFlowPoint> Put);
