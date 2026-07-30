namespace SpreadTrader.Core.Models;

public enum TriggerOperator
{
    GreaterThanOrEqual,
    LessThanOrEqual,

    /// <summary>Formerly "watch both sides at once" (paired with the now-removed
    /// AutoDirectionFromSpreadSign, which decided which leg to buy/sell live from the spread's
    /// actual direction at fire time). No current UI can create a rule with this operator any
    /// more - kept as an enum member (rather than deleted) purely so a pre-existing row in
    /// triggers.csv that still has Operator=AbsoluteGreaterThanOrEqual continues to parse instead
    /// of being dropped as unparseable; its condition (|measured| &gt;= Threshold) still evaluates
    /// correctly in SpreadCalculationHostedService, but since leg actions now always come from the
    /// fixed NearLegAction/FarLegAction fields (see OrderDispatchHostedService.BuildLegs), any such
    /// legacy row fires with whatever fixed legs it happens to have stored - None/None for a row
    /// that predates this removal, so it fires but places no orders.</summary>
    AbsoluteGreaterThanOrEqual,
}

public enum TriggerLegAction
{
    None,
    Buy,
    Sell
}

/// <summary>
/// A user-configured condition on a spread's value. When the spread crosses Threshold in the
/// direction given by Operator, the engine fires a basket order using the leg actions below.
/// Persisted to a CSV file by default - see CsvTriggerRuleStore.
/// </summary>
public sealed class TriggerRule
{
    public required string Id { get; set; }
    public required string SpreadId { get; set; }
    public TriggerOperator Operator { get; set; }
    public decimal Threshold { get; set; }

    /// <summary>When true, Threshold is read as a percentage of the near leg's ask price
    /// (SpreadSnapshot.SpreadPercent) instead of an absolute rupee price difference
    /// (SpreadSnapshot.SpreadValue). Needed for a single rule template to make sense across many
    /// stocks trading at very different price levels - see the bulk trigger template endpoint.
    /// </summary>
    public bool ThresholdIsPercent { get; set; }

    // What to do on each leg when the rule fires. Set to None to leave a leg untouched. Always
    // used as-is now - the old "auto-direction" mode that ignored these and decided live from the
    // spread's sign at fire time was removed (see OrderDispatchHostedService.BuildLegs).
    public TriggerLegAction NearLegAction { get; set; } = TriggerLegAction.None;
    public TriggerLegAction FarLegAction { get; set; } = TriggerLegAction.None;

    public int Quantity { get; set; }
    public string Product { get; set; } = "NRML"; // NRML, MIS, CNC
    public string OrderType { get; set; } = "MARKET"; // MARKET, LIMIT

    /// <summary>How far past the current best bid/ask (as a percent of that price) MarketProtectionService
    /// is allowed to price the protected LIMIT order it substitutes for a MARKET leg (Kite rejects plain
    /// MARKET orders on F&amp;O - see MarketProtectionService's doc comment). User-chosen per rule instead of
    /// a single app-wide EngineOptions.MarketProtectionPercent, since a thinly-traded far-month contract
    /// may need more headroom to fill immediately than a liquid near-month one. Applied identically to
    /// BOTH legs when the rule fires - each leg is protected off its own reference price (its own best
    /// bid/ask), so e.g. 0.10% widens the near leg's fill price by 0.10% of the near leg's price AND the
    /// far leg's fill price by 0.10% of the far leg's (different rupee amount, same percentage).</summary>
    public decimal SlippagePercent { get; set; } = 0.10m;

    /// <summary>If true, the rule disables itself after firing once. If false, see MaxFireCount -
    /// a bounded number of sequential shots is now the only repeat-firing mode this app offers
    /// (the older "stand indefinitely, re-arming once the spread retreats past a buffer" mode was
    /// removed - see MaxFireCount's doc comment and SpreadCalculationHostedService.
    /// EvaluateTriggersAsync, which no longer has any re-arm/hysteresis logic at all).</summary>
    public bool OneShot { get; set; } = true;

    /// <summary>Bounded "N sequential shots" mode - only consulted when OneShot is false; 0 means
    /// the rule simply won't fire again after a manual re-enable resets it (see
    /// TriggersController.Enable, which sets IsArmed back to true). A positive value schedules
    /// exactly that many total fires with no re-arm/hysteresis requirement: as soon as the
    /// condition is true again AND the previous shot's basket dispatch has resolved (filled or
    /// rejected - see TriggerInFlightTracker) AND EngineOptions.MinFireIntervalPerRuleMs has
    /// elapsed, the next shot fires immediately - it does NOT wait for the spread to retreat first.
    /// If the condition happens to be false when checked, that check is simply skipped without
    /// losing progress; the count picks back up next time the condition is true again. The rule
    /// disables itself once FireCount reaches MaxFireCount, same as a plain one-shot rule does
    /// after its single fire.</summary>
    public int MaxFireCount { get; set; }

    /// <summary>How many of MaxFireCount's scheduled shots have fired so far. Persisted so a
    /// restart doesn't lose progress or re-fire shots that already went out. Only meaningful when
    /// MaxFireCount > 0.</summary>
    public int FireCount { get; set; }

    /// <summary>Consecutive fires in a row where nothing filled at all (fully rejected/blocked -
    /// see OrderDispatchHostedService.RestoreRuleIfNothingFilled). Reset to 0 the instant any fire
    /// actually fills something. Once this reaches EngineOptions.MaxUnfilledDispatchAttempts, the
    /// rule is auto-disarmed (IsArmed/Enabled both set false) instead of being handed its shot
    /// back again, so a condition that can never fill (chronic insufficient margin, a frozen/
    /// delisted instrument, a persistently rejected order) stops retrying instead of doing so
    /// forever - a human has to look at it and re-enable it (see TriggerRuleControllerBase.Enable,
    /// which also resets this back to 0).</summary>
    public int UnfilledAttempts { get; set; }

    public bool Enabled { get; set; } = true;

    // Mutable runtime state - not thread-safe on its own, only touched under the store's lock.
    public bool IsArmed { get; set; } = true;
    public DateTimeOffset? LastFiredAt { get; set; }
}
