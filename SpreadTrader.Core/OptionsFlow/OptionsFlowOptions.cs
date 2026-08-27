namespace SpreadTrader.Core.OptionsFlow;

/// <summary>
/// Feature flag + configuration for the index-options CDV/OBV add-on (the "Options Flow" page).
///
/// The ENTIRE feature is gated behind <see cref="Enabled"/>, which defaults to false. While it is
/// false nothing in this class is consulted, no hosted service is registered, no option contracts
/// are parsed or subscribed, and the app behaves exactly as the futures-only engine always has.
/// That isolation is the point: this add-on shares only the broker feed connection with the
/// existing engine, and nothing else.
///
/// What it computes, per index, over a strike window centred on ATM:
///  - CDV (cumulative volume delta): per 1-minute bar, buy volume minus sell volume, estimated
///    from where the bar closed within its own range - see OptionFlowMath.
///  - OBV (on-balance volume): per bar, the full volume signed by the direction of the close.
/// Both are optionally weighted by the contract's delta, then summed across strikes into one CE
/// series and one PE series - which is what the dashboard plots.
/// </summary>
public sealed class OptionsFlowOptions
{
    public const string SectionName = "OptionsFlow";

    /// <summary>Master on/off switch. False (the default) means the feature does not exist at
    /// runtime - see the class doc comment.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Which index option chains to track. Each becomes an independent series pair
    /// (CE and PE) that the dashboard can switch between.
    ///
    /// Deliberately left EMPTY rather than defaulted in the initializer: .NET's configuration
    /// binder APPENDS to an existing List&lt;T&gt; instead of replacing it, so a property seeded
    /// with defaults here ends up holding the defaults AND the configured values ("NIFTY",
    /// "BANKNIFTY", "NIFTY", "BANKNIFTY") whenever appsettings names the same ones. Read this
    /// through <see cref="ResolveIndices"/>, which supplies the default only when nothing was
    /// configured.</summary>
    public List<string> Indices { get; set; } = new();

    /// <summary>The indices actually tracked: whatever was configured, de-duplicated, or the
    /// built-in default when the section names none. See <see cref="Indices"/> for why the
    /// defaulting can't live in the property initializer.</summary>
    public IReadOnlyList<string> ResolveIndices() =>
        Indices.Count > 0
            ? Indices.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { "NIFTY", "BANKNIFTY" };

    /// <summary>How many strikes BELOW ATM to include (the reference Pine script uses 8).</summary>
    public int StrikesBelowAtm { get; set; } = 8;

    /// <summary>How many strikes ABOVE ATM to include (the reference Pine script uses 7).</summary>
    public int StrikesAboveAtm { get; set; } = 7;

    /// <summary>How often ATM is re-evaluated and the strike window re-centred. On a shift the
    /// whole day's series is rebuilt from <see cref="SessionStartIst"/> over the NEW window rather
    /// than spliced onto the old one - see OptionsFlowEngine. Set to 0 to pin the window to
    /// whatever ATM was at the first evaluation of the day, which is what the Pine script does
    /// with its manual ATM input.</summary>
    public int AtmShiftIntervalMinutes { get; set; } = 60;

    /// <summary>Weight each contract's volume by its (approximate) delta before summing across
    /// strikes, so a far-OTM strike's volume doesn't count the same as an ATM strike's. Mirrors
    /// the reference script's "Use Delta Weighting" toggle, including its approximation - see
    /// OptionFlowMath.ApproximateDelta.</summary>
    public bool UseDeltaWeighting { get; set; } = true;

    /// <summary>EMA smoothing applied to the aggregated series, in bars. Mirrors the reference
    /// script's "CDV EMA Length".</summary>
    public int SmoothingLength { get; set; } = 9;

    /// <summary>Indian market open, IST. Every series starts here each day - both the historical
    /// backfill's fromDate and the point at which the previous day's data is dropped.</summary>
    public string SessionStartIst { get; set; } = "09:15";

    /// <summary>Indian market close, IST. Nothing is accumulated past this.</summary>
    public string SessionEndIst { get; set; } = "15:30";

    /// <summary>How often the live (still-open) minute bar is recomputed from the tick store and
    /// pushed to connected dashboards. The closed bars never change; this only moves the tip of
    /// the curve. 1000ms is the "in-memory data every second" behaviour.</summary>
    public int LiveRefreshIntervalMs { get; set; } = 1000;

    /// <summary>Backfill each strike's history from the session start when the window is built or
    /// re-centred. Requires a broker that serves intraday history for options - Dhan does
    /// (POST /v2/charts/intraday); Kite's historical API is a paid add-on, so on Kite this
    /// degrades to accumulating from whenever the service started. See IOptionHistoryProvider.</summary>
    public bool BackfillFromSessionStart { get; set; } = true;
}
