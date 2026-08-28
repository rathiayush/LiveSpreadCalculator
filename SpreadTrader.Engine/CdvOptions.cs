namespace SpreadTrader.Engine;

/// <summary>
/// Configuration for CdvHostedService - the delta-weighted Cumulative Delta Volume (CDV) feed for
/// an index option chain's CE/PE order flow, ported from the "delta auto with Delta-Weighted CDV"
/// Pine Script indicator. See CdvHostedService's class doc comment for how each Pine concept maps
/// onto this app's real tick data.
/// </summary>
public sealed class CdvOptions
{
    public const string SectionName = "Cdv";

    /// <summary>Master switch. When false CdvHostedService does nothing - no subscriptions, no
    /// accumulation, no publishing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which underlyings to run a CDV chain for. Dhan-only (see
    /// DhanInstrumentMasterService's option-chain parsing) and index-options-only (OPTIDX) - only
    /// "NIFTY"/"BANKNIFTY" are meaningful today; an unrecognised name is simply skipped (logged
    /// once) since there is no strike-step mapping for it - see CdvHostedService.StrikeStepFor.</summary>
    public List<string> Underlyings { get; set; } = new() { "NIFTY", "BANKNIFTY" };

    /// <summary>The synthetic bar size CDV is sampled on - the direct counterpart of the Pine
    /// script's chart timeframe, since "lookback N bars" and "EMA length N" are both bar-indexed,
    /// not time-indexed. 60s by default (roughly a 1-minute chart).</summary>
    public int TimeframeSeconds { get; set; } = 60;

    /// <summary>ta.ema(..., smoothLen) in the Pine script - CdvHostedService.SmoothLen.</summary>
    public int SmoothLen { get; set; } = 9;

    /// <summary>Pine's lookbackBars input - how many bars ago the single "Lookback %" comparison
    /// looks back.</summary>
    public int LookbackBars { get; set; } = 10;

    /// <summary>Pine's lb1Bars/lb2Bars/lb3Bars - the horizontal multi-lookback table's three
    /// windows.</summary>
    public List<int> MultiLookbackBars { get; set; } = new() { 20, 25, 30 };

    /// <summary>Pine's lookbackPctThreshold - |% change| at/above this over LookbackBars bars is a
    /// "hit" (highlighted, alerted).</summary>
    public decimal LookbackPctThreshold { get; set; } = 50m;

    /// <summary>Pine's frozenPctThreshold - same idea, measured against the frozen-bar value.</summary>
    public decimal FrozenPctThreshold { get; set; } = 50m;

    /// <summary>Pine's frozenTimeInput (HH:mm, IST) - the clock time whose bar's EMA value becomes
    /// the day's frozen baseline for FrozenPercent/FrozenHit.</summary>
    public string FrozenTimeIst { get; set; } = "13:42";

    /// <summary>Pine's useDeltaWeighting - weight each strike's volume delta by
    /// |sigmoid-moneyness delta proxy| before summing into the underlying's CE/PE total. See
    /// CdvHostedService.DeltaWeight for the exact (Pine-identical) formula.</summary>
    public bool UseDeltaWeighting { get; set; } = true;

    /// <summary>How many strikes either side of ATM are summed into the published CE/PE
    /// totals/chart - Pine's fixed ATM±8.</summary>
    public int DisplayStrikeRange { get; set; } = 8;

    /// <summary>How many strikes either side of ATM are actually subscribed and accumulated -
    /// deliberately wider than DisplayStrikeRange so a periodic ATM re-centre (see
    /// AtmRecenterIntervalMinutes) can usually just change which already-running strikes are
    /// summed, with full same-day bar history already in hand for the newly-displayed strikes -
    /// see CdvHostedService's class doc comment on "recompute the chart from history, don't reset
    /// it".</summary>
    public int SubscribeStrikeRange { get; set; } = 15;

    /// <summary>How often (in minutes) EnsureChainAsync re-checks the chain: recomputes the ATM
    /// strike from the live spot price AND re-resolves the nearest listed option expiry (in case a
    /// weekly expiry rolled to the next one since the last check), re-centring the display window
    /// on both. The Pine script's ATM is a fixed manual input for the whole session; this app has
    /// no such input (spot is live), so instead of updating on every tick - which would make "the
    /// chart" mean something different bar to bar - it is refreshed on this fixed cadence, matching
    /// how an operator would occasionally nudge the Pine input by hand. Default 30 minutes.</summary>
    public int AtmRecenterIntervalMinutes { get; set; } = 30;
}
