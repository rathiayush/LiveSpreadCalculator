namespace SpreadTrader.Core.Models;

public enum TriggerOperator
{
    GreaterThanOrEqual,
    LessThanOrEqual,

    /// <summary>Watches both sides at once: fires when the absolute value of the measured
    /// spread (SpreadValue or SpreadPercent - see TriggerRule.ThresholdIsPercent) reaches
    /// Threshold, regardless of which direction it moved. Threshold is stored as a positive
    /// magnitude for this mode (see TriggersController.Create) - there's no sign to get wrong,
    /// since which leg to buy/sell is decided live from the spread's actual direction at fire
    /// time (AutoDirectionFromSpreadSign), completely independent of which side of the
    /// threshold triggered the fire. Previously, catching a deviation on either side required
    /// two separate signed rules (e.g. +8 and -8) - see BulkTriggerTemplateService's BULK-UP/
    /// BULK-DOWN pair, which still uses that two-rule approach for bulk-created rules.</summary>
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
    /// The calendar-spread arbitrage rule: when true, NearLegAction/FarLegAction above are
    /// ignored and instead taken from the Direction that SpreadCalculationHostedService already
    /// decided when it computed the snapshot that crossed the threshold (carried through via
    /// TriggerEvent.Direction, not re-derived from the spread value's sign - see the comment on
    /// TriggerEvent for why):
    ///   - near (current month) leg in premium -> sell the near leg, buy the far (next month) leg
    ///   - near (current month) leg at a discount -> buy the near leg, sell the far leg
    /// i.e. sell whichever leg is trading rich and buy whichever leg is trading cheap, so the
    /// position profits as the spread converges. This is the default for rules created from the
    /// trigger-creation page, since most users want "fire off which leg is rich/cheap", not a
    /// fixed direction baked in ahead of time.
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
