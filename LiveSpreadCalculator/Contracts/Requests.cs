using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Contracts;

/// <summary>App-login credentials posted to POST /api/auth/token. Validated against
/// App_Data/users.csv (see CsvUserStore); on success the caller gets a JWT to send as a
/// Bearer token on every subsequent API/hub call.</summary>
public sealed class UserLoginRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}

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

    /// <summary>Signed threshold when WatchBothSides is false (the default) - by convention
    /// (and, unless FireWhenGreaterOrEqual is set - see below - the ONLY way TriggersController
    /// can tell), positive fires when the spread rises to/above this value, negative fires when
    /// it falls to/below this value. When WatchBothSides is true, send this as a plain positive
    /// magnitude instead (its sign is ignored - see WatchBothSides).</summary>
    public decimal Threshold { get; set; }

    /// <summary>Overrides the sign-based Operator inference above. Needed for a target that
    /// doesn't fit "positive = rising, negative = falling": an exit rule protecting a position
    /// entered at, say, spread -15 and meant to fire once it's recovered to -7 needs
    /// GreaterThanOrEqual with a NEGATIVE threshold (-7) - the opposite of what sign-inference
    /// would produce (a negative threshold would otherwise always mean LessThanOrEqual). See
    /// AddPositionExitTriggerPage.jsx, which always sends this explicitly rather than relying on
    /// Threshold's sign. Null (the default, and what TriggerForm.jsx's ordinary Add Trigger page
    /// still sends) preserves the original sign-inference behavior for backward compatibility.
    /// Ignored when WatchBothSides is true.</summary>
    public bool? FireWhenGreaterOrEqual { get; set; }

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

    /// <summary>How far past the best bid/ask (as a percent) MarketProtectionService may price the
    /// protected LIMIT order it substitutes for a MARKET leg when this rule fires - applied to
    /// BOTH legs, each off its own reference price. User-chosen in the trigger-creation UI (one
    /// of 0.05/0.10/0.15/0.20/0.25/0.30) instead of a single app-wide setting - see
    /// TriggerRule.SlippagePercent.</summary>
    public decimal SlippagePercent { get; set; } = 0.10m;

    /// <summary>Set only when this rule is created from the Positions & PNL page's "Add trigger"
    /// action on an open position (see AddPositionExitTriggerPage.jsx) - identifies the exact
    /// position this exit rule protects, so PositionsController.SquareOff can find and delete it
    /// automatically the moment that position is manually squared off. Leave all three null for
    /// an ordinary entry-style rule created from the Triggers page - see TriggerRule.
    /// LinkedPositionTradingSymbol.</summary>
    public string? LinkedPositionTradingSymbol { get; set; }
    public string? LinkedPositionExchange { get; set; }
    public string? LinkedPositionProduct { get; set; }
}

/// <summary>Retunes an existing trigger's lots/threshold in place (leaving its id, enabled/armed
/// state, and fire history untouched) - see PUT /api/triggers/{id} / TriggersController.Update.
/// Lots is converted to an actual quantity the same way CreateTriggerRuleRequest.Lots is.</summary>
public sealed class UpdateTriggerRuleRequest
{
    public int Lots { get; set; }
    public decimal Threshold { get; set; }
    public bool ThresholdIsPercent { get; set; }

    /// <summary>Same escape hatch as CreateTriggerRuleRequest.FireWhenGreaterOrEqual - lets the
    /// generic Edit modal (TriggerList.jsx) retune an exit trigger's raw signed target (e.g. -8)
    /// without going through the ordinary magnitude+sign-flip convention, which can't represent a
    /// negative target the spread needs to RISE to. Null (what the ordinary trigger edit flow
    /// still sends) preserves the original sign-of-Threshold inference.</summary>
    public bool? FireWhenGreaterOrEqual { get; set; }

    /// <summary>Same convention as CreateTriggerRuleRequest.SlippagePercent - null leaves the
    /// rule's existing SlippagePercent untouched, so callers that don't offer a slippage editor
    /// don't need to round-trip the current value just to patch lots/threshold.</summary>
    public decimal? SlippagePercent { get; set; }
}

/// <summary>Exits (fully or partially) an existing open position - see POST
/// /api/positions/square-off / PositionsController.SquareOff. Identifies the position by
/// symbol+exchange+product (Kite requires the product to match to net down the same position)
/// rather than by taking a signed quantity/side from the client, so the controller can derive
/// the correct opposite transaction_type (SELL to cut a long, BUY to cover a short) itself
/// instead of trusting the caller to get that right.</summary>
public sealed class SquareOffPositionRequest
{
    public required string TradingSymbol { get; set; }
    public required string Exchange { get; set; }
    public string Product { get; set; } = "NRML";

    /// <summary>Number of exchange lots to exit, not raw share/contract quantity - same
    /// convention as CreateTriggerRuleRequest.Lots. Converted to an actual quantity using the
    /// position's own resolved LotSize (from GET /api/positions) and capped at however much of
    /// the position is actually open.</summary>
    public required int Lots { get; set; }
}

/// <summary>One template applied across many spreads at once - see
/// BulkTriggerTemplateService / POST /api/triggers/bulk. Creates ONE rule per target spread now
/// (previously two auto-direction rules, one per side): a plain signed target spread % plus an
/// explicit comparison and a FIXED Buy/Sell/None leg action pair applied identically to every
/// spread in the batch - matches the single-spread Add Trigger form's design (see TriggerForm.jsx/
/// CreateTriggerRuleRequest), traded off deliberately against auto-direction's per-stock accuracy:
/// this can't know which leg is actually rich/cheap for each different underlying, so the same
/// fixed legs are used everywhere regardless.</summary>
public sealed class BulkTriggerTemplateRequest
{
    /// <summary>Spread ids to apply the template to. Leave empty/null to apply to every
    /// currently enabled spread (e.g. the whole built universe after a POST /api/universe/build).</summary>
    public List<string>? SpreadIds { get; set; }

    /// <summary>Signed target spread %, applied identically to every spread in the batch (e.g.
    /// -2 fires once a given spread's % reaches -2, regardless of that stock's price level -
    /// percent, not a rupee amount, is what makes one number meaningful across many different
    /// underlyings). NOT a deviation magnitude - the raw value compared directly against
    /// SpreadSnapshot.SpreadPercent, same convention as CreateTriggerRuleRequest.Threshold.</summary>
    public decimal TargetPercent { get; set; }

    /// <summary>True = fires when a spread's % rises to/above TargetPercent (GreaterThanOrEqual),
    /// false = falls to/below it (LessThanOrEqual) - explicit, not inferred from TargetPercent's
    /// sign (the same escape hatch as CreateTriggerRuleRequest.FireWhenGreaterOrEqual, needed for
    /// e.g. a negative target the spread needs to RISE to).</summary>
    public bool FireWhenGreaterOrEqual { get; set; } = true;

    /// <summary>Fixed leg actions applied identically to every spread in the batch - at least one
    /// must be Buy or Sell (see BulkTriggerTemplateService.Apply).</summary>
    public TriggerLegAction NearLegAction { get; set; } = TriggerLegAction.None;
    public TriggerLegAction FarLegAction { get; set; } = TriggerLegAction.None;

    public int LotsPerTrade { get; set; } = 1;
    public string Product { get; set; } = "NRML";
    public string OrderType { get; set; } = "MARKET";
    public bool OneShot { get; set; } = true;
    public decimal RearmBufferPercent { get; set; }

    /// <summary>Same convention as CreateTriggerRuleRequest.SlippagePercent, applied identically
    /// to every rule created by this batch.</summary>
    public decimal SlippagePercent { get; set; } = 0.10m;
}
