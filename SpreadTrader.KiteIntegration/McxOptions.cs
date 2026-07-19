namespace SpreadTrader.KiteIntegration;

/// <summary>
/// Feature flag + configuration for the MCX commodity calendar/cross-contract arbitrage add-on
/// (silver, gold, crude oil, natural gas and their mini/micro variants). The entire feature is
/// gated behind <see cref="Enabled"/>: when it is false (the default), NOTHING in this class is
/// consulted and every code path behaves exactly as the NFO-only app always has - the instrument
/// master fetches only /instruments/NFO, positions stay NFO-scoped, and the margin pre-check uses
/// the equity segment. Flip Enabled to true (via appsettings.json's "Mcx" section, or an
/// Mcx__Enabled environment variable) to switch the whole thing on without touching any business
/// logic.
///
/// MCX differs from NFO in two ways this add-on cares about:
///  1. It is a separate exchange, so its futures live under a different Kite instrument-master
///     endpoint (/instruments/MCX) and its positions come back with exchange == "MCX".
///  2. It settles against the "commodity" margin segment, not "equity" - see MarginSegment and
///     OrderDispatchHostedService's pre-dispatch margin check.
/// </summary>
public sealed class McxOptions
{
    public const string SectionName = "Mcx";

    /// <summary>Master on/off switch for the entire MCX add-on. Default false = the app stays
    /// NFO-only and byte-for-byte unchanged. Everything else in this class is ignored while this
    /// is false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>The Kite exchange code for commodities. Only overridden in the extremely unlikely
    /// event Kite renames the segment; otherwise leave as "MCX".</summary>
    public string Exchange { get; set; } = "MCX";

    /// <summary>Kite's funds/margin segment MCX contracts settle against. NFO (equity F&amp;O)
    /// uses "equity"; commodities use "commodity". Used by the pre-dispatch available-margin check
    /// whenever a basket contains at least one MCX leg - see
    /// OrderDispatchHostedService.CheckMarginAsync. https://kite.trade/docs/connect/v3/user/#funds-and-margins</summary>
    public string MarginSegment { get; set; } = "commodity";

    /// <summary>When true, the MCX universe build pairs each commodity's own near-expiry vs
    /// far-expiry futures into a calendar spread (e.g. SILVER 5-Aug vs SILVER 5-Sep) - the same
    /// idea as the NFO engine, just on MCX. This is the classic silver calendar arbitrage.</summary>
    public bool CalendarSpreads { get; set; } = true;

    /// <summary>When true, the MCX universe build also pairs the different contract specifications
    /// of the SAME commodity against each other (e.g. SILVER vs SILVERM vs SILVERMIC), letting the
    /// engine exploit the price difference between the big-lot and mini/micro contracts of one
    /// metal - see CrossContractGroups.</summary>
    public bool CrossContractSpreads { get; set; } = true;

    /// <summary>How the two legs of a cross-contract pair are matched on expiry. The big contract
    /// and its mini/micro siblings often list DIFFERENT expiry cycles (e.g. on MCX the 30kg SILVER
    /// lists Sep/Dec while SILVERM/SILVERMIC list Aug/Nov), so requiring an identical expiry would
    /// skip exactly the SILVER-vs-mini pairs traders care about. Instead the build picks the pair
    /// of listed contracts whose expiries are CLOSEST together (gap 0 = a true same-expiry match,
    /// used whenever both specs happen to share a month), and rejects the pair entirely if even the
    /// closest expiries are more than this many days apart - because past that point the spread is
    /// dominated by calendar carry, not the per-kg spec mispricing this pairing is meant to
    /// capture. Raise it to allow wider-apart pairings, lower it to insist on near-identical
    /// expiries. Only consulted when CrossContractSpreads is true.</summary>
    public int MaxCrossContractExpiryGapDays { get; set; } = 45;

    /// <summary>Every MCX commodity underlying to fetch and (if CalendarSpreads is on) build a
    /// near/far calendar spread for. These are Kite instrument-master "name" values, not
    /// tradingsymbols. Seeded with the common liquid contracts; trim or extend freely - nothing
    /// here is hard-coded anywhere else.</summary>
    public List<string> Underlyings { get; set; } = new()
    {
        "SILVER", "SILVERM", "SILVERMIC",
        "GOLD", "GOLDM",
        "CRUDEOIL", "CRUDEOILM",
        "NATURALGAS",
    };

    /// <summary>Groups of contract specifications of the same commodity to arbitrage against each
    /// other when CrossContractSpreads is on. Each group's members are paired front-month vs
    /// front-month, every distinct pair within the group (e.g. SILVER-SILVERM, SILVER-SILVERMIC,
    /// SILVERM-SILVERMIC). Members not present in the live instrument master are skipped.</summary>
    public List<McxContractGroup> CrossContractGroups { get; set; } = new()
    {
        new McxContractGroup { Legs = new() { "SILVER", "SILVERM", "SILVERMIC" } },
        new McxContractGroup { Legs = new() { "GOLD", "GOLDM" } },
        new McxContractGroup { Legs = new() { "CRUDEOIL", "CRUDEOILM" } },
    };

    /// <summary>All exchanges the instrument master should load: always NFO, plus MCX when this
    /// feature is enabled. Central helper so the "NFO + maybe MCX" decision lives in exactly one
    /// place.</summary>
    public IReadOnlyList<string> ResolveExchanges() =>
        Enabled ? new[] { "NFO", Exchange } : new[] { "NFO" };
}

/// <summary>One set of same-commodity contract specs to arbitrage against each other - see
/// McxOptions.CrossContractGroups.</summary>
public sealed class McxContractGroup
{
    public List<string> Legs { get; set; } = new();
}
