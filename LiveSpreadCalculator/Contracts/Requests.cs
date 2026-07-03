using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Contracts;

public sealed class InstrumentLegRequest
{
    public required string TradingSymbol { get; set; }
    public required string Exchange { get; set; }
}

public sealed class CreateSpreadRequest
{
    public required string Id { get; set; }
    public required string Underlying { get; set; }
    public required InstrumentLegRequest NearLeg { get; set; }
    public required InstrumentLegRequest FarLeg { get; set; }
}

public sealed class CreateTriggerRuleRequest
{
    public required string Id { get; set; }
    public required string SpreadId { get; set; }

    /// <summary>Signed threshold when WatchBothSides is false (the default) - positive fires
    /// when the spread rises to/above this value (premium widening), negative fires when it
    /// falls to/below this value (discount widening). TriggersController derives
    /// TriggerRule.Operator from this value's sign in that case, so the client doesn't send an
    /// operator separately. When WatchBothSides is true, send this as a plain positive
    /// magnitude instead (its sign is ignored - see WatchBothSides).</summary>
    public decimal Threshold { get; set; }

    /// <summary>When true, Threshold/RearmBuffer are a percentage of the near leg's ask price
    /// instead of an absolute rupee difference.</summary>
    public bool ThresholdIsPercent { get; set; }

    /// <summary>When true, Threshold is treated as an unsigned magnitude and the rule fires
    /// when the spread deviates that much in EITHER direction (TriggerOperator.
    /// AbsoluteGreaterThanOrEqual) - no sign to guess at, since AutoDirectionFromSpreadSign
    /// (required when this is true - see TriggersController.Create) decides which leg to buy/
    /// sell live from the spread's actual direction at fire time regardless of which side
    /// crossed the threshold. False (the default) keeps the one-sided signed-threshold
    /// behavior.</summary>
    public bool WatchBothSides { get; set; }

    public TriggerLegAction NearLegAction { get; set; } = TriggerLegAction.None;
    public TriggerLegAction FarLegAction { get; set; } = TriggerLegAction.None;

    /// <summary>When true (the default), NearLegAction/FarLegAction are ignored and the engine
    /// instead buys the current-month leg / sells the next-month leg when the spread value is
    /// negative at fire time, and does the opposite when it's positive.</summary>
    public bool AutoDirectionFromSpreadSign { get; set; } = true;

    /// <summary>Number of exchange lots, not raw share/contract quantity - Kite (and NSE F&amp;O
    /// generally) only accepts order quantities that are whole multiples of the contract's lot
    /// size, so this is converted to an actual quantity (Lots * the spread's resolved lot size)
    /// by TriggersController.Create before it's stored on TriggerRule.Quantity - matching how
    /// BulkTriggerTemplateService already does it for bulk-created rules.</summary>
    public int Lots { get; set; }

    public string Product { get; set; } = "NRML";
    public string OrderType { get; set; } = "MARKET";
    public bool OneShot { get; set; } = true;
    public decimal RearmBuffer { get; set; }
}

/// <summary>Retunes an existing trigger's lots/threshold in place (leaving its id, enabled/armed
/// state, and fire history untouched) - see PUT /api/triggers/{id} / TriggersController.Update.
/// Lots is converted to an actual quantity the same way CreateTriggerRuleRequest.Lots is.</summary>
public sealed class UpdateTriggerRuleRequest
{
    public int Lots { get; set; }
    public decimal Threshold { get; set; }
    public bool ThresholdIsPercent { get; set; }
}

/// <summary>One template applied across many spreads at once - see
/// BulkTriggerTemplateService / POST /api/triggers/bulk.</summary>
public sealed class BulkTriggerTemplateRequest
{
    /// <summary>Spread ids to apply the template to. Leave empty/null to apply to every
    /// currently enabled spread (e.g. the whole built universe after a POST /api/universe/build).</summary>
    public List<string>? SpreadIds { get; set; }

    public decimal ThresholdPercent { get; set; }
    public int LotsPerTrade { get; set; } = 1;
    public string Product { get; set; } = "NRML";
    public string OrderType { get; set; } = "MARKET";
    public bool OneShot { get; set; } = true;
    public decimal RearmBufferPercent { get; set; }
}
