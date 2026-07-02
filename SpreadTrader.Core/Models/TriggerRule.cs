namespace SpreadTrader.Core.Models;

public enum TriggerOperator
{
    GreaterThanOrEqual,
    LessThanOrEqual
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

    /// <summary>When true, Threshold and RearmBuffer are read as a percentage of the near leg's
    /// ask price (SpreadSnapshot.SpreadPercent) instead of an absolute rupee price difference
    /// (SpreadSnapshot.SpreadValue). Needed for a single rule template to make sense across many
    /// stocks trading at very different price levels - see the bulk trigger template endpoint.
    /// </summary>
    public bool ThresholdIsPercent { get; set; }

    // What to do on each leg when the rule fires. Set to None to leave a leg untouched. Ignored
    // when AutoDirectionFromSpreadSign is true (see below).
    public TriggerLegAction NearLegAction { get; set; } = TriggerLegAction.None;
    public TriggerLegAction FarLegAction { get; set; } = TriggerLegAction.None;

    /// <summary>
    /// The calendar-spread carry rule: when true, NearLegAction/FarLegAction above are ignored
    /// and instead computed at fire time from the sign of the spread value that actually crossed
    /// the threshold - see SpreadCalculationHostedService.RunCycleAsync for exactly how that
    /// value is priced (the leg you'd buy at its ask, the leg you'd sell at its bid, not a flat
    /// ask-vs-ask comparison):
    ///   - spread value negative -> buy the near (current month) leg, sell the far (next month) leg
    ///   - spread value positive -> sell the near (current month) leg, buy the far (next month) leg
    /// This is the default for rules created from the trigger-creation page, since most users
    /// want "fire off the sign of the move", not a fixed direction baked in ahead of time.
    /// </summary>
    public bool AutoDirectionFromSpreadSign { get; set; } = true;

    public int Quantity { get; set; }
    public string Product { get; set; } = "NRML"; // NRML, MIS, CNC
    public string OrderType { get; set; } = "MARKET"; // MARKET, LIMIT

    /// <summary>If true, the rule disables itself after firing once. If false, it re-arms once
    /// the spread moves back across the threshold by RearmBufferValue.</summary>
    public bool OneShot { get; set; } = true;
    public decimal RearmBuffer { get; set; }

    public bool Enabled { get; set; } = true;

    // Mutable runtime state - not thread-safe on its own, only touched under the store's lock.
    public bool IsArmed { get; set; } = true;
    public DateTimeOffset? LastFiredAt { get; set; }
}
