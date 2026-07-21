using Microsoft.AspNetCore.Mvc;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Everything a trigger-rule CRUD endpoint needs (Create/Update/Enable/Disable/Delete/DeleteAll/
/// GetAll), shared verbatim between TriggersController (entry rules, ITriggerRuleStore) and
/// ExitTriggersController (exit rules, IExitTriggerRuleStore) - see IExitTriggerRuleStore's doc
/// comment for why entry and exit rules live in physically separate stores rather than one shared
/// store with a type flag. Only the store instance differs between the two subclasses (injected
/// via their own constructors and handed up to this base) - every validation rule, lot-size
/// resolution, and CSV-lock error-handling concern below applies identically either way, since an
/// exit trigger fires and dispatches orders through exactly the same engine path as any other
/// rule. Activity feed (GetActivity/ClearActivity) and bulk template application stay on
/// TriggersController only - see that class for why.
/// </summary>
public abstract class TriggerRuleControllerBase : ControllerBase
{
    private readonly ITriggerRuleStore _ruleStore;
    private readonly ISpreadDefinitionStore _spreadStore;

    protected TriggerRuleControllerBase(ITriggerRuleStore ruleStore, ISpreadDefinitionStore spreadStore)
    {
        _ruleStore = ruleStore;
        _spreadStore = spreadStore;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<TriggerRule>> GetAll([FromQuery] string? spreadId) =>
        Ok(string.IsNullOrEmpty(spreadId) ? _ruleStore.GetAll() : _ruleStore.GetForSpread(spreadId));

    [HttpPost]
    public ActionResult<TriggerRule> Create([FromBody] CreateTriggerRuleRequest request)
    {
        var spread = _spreadStore.Get(request.SpreadId);
        if (spread is null)
            return BadRequest($"Unknown spread id: {request.SpreadId}");

        if (request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set - otherwise the rule would fire and place no orders.");

        if (request.Lots <= 0)
            return BadRequest("Lots must be greater than zero.");

        if (request.SlippagePercent <= 0)
            return BadRequest("SlippagePercent must be greater than zero.");

        if (request.MaxFireCount < 0)
            return BadRequest("MaxFireCount cannot be negative.");

        // Near and far month contracts on the same underlying always share the same lot size,
        // so either leg's resolved LotSize works - fall back to the far leg in case only that
        // one has been resolved yet. TriggerRule.Quantity is always the real, exchange-valid
        // share/contract quantity from here on (Lots * LotSize) - see BulkTriggerTemplateService,
        // which does the same conversion for bulk-created rules.
        var lotSize = spread.NearLeg.LotSize > 0 ? spread.NearLeg.LotSize : spread.FarLeg.LotSize;
        if (lotSize <= 0)
            return BadRequest("Lot size not resolved yet for this spread - build/resolve the universe first.");

        var rule = new TriggerRule
        {
            Id = request.Id,
            SpreadId = request.SpreadId,
            // FireWhenGreaterOrEqual, if the client sent one, wins outright - it exists
            // specifically because sign-inference can't represent every valid rule (a negative
            // target you're waiting for the spread to RISE to, e.g. an exit rule recovering from
            // -15 toward -7 - see CreateTriggerRuleRequest.FireWhenGreaterOrEqual). Falls back to
            // the original sign-of-Threshold inference when it's null, for every existing caller
            // (TriggerForm.jsx's ordinary Add Trigger page) that never sends it.
            Operator = request.FireWhenGreaterOrEqual is { } explicitDirection
                ? (explicitDirection ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual)
                : request.Threshold >= 0 ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual,
            Threshold = request.Threshold,
            ThresholdIsPercent = request.ThresholdIsPercent,
            NearLegAction = request.NearLegAction,
            FarLegAction = request.FarLegAction,
            Quantity = request.Lots * lotSize,
            Product = request.Product,
            OrderType = request.OrderType,
            OneShot = request.OneShot,
            MaxFireCount = request.MaxFireCount,
            SlippagePercent = request.SlippagePercent,
        };

        if (!TryUpsert(rule, out var problem)) return problem!;
        return Ok(rule);
    }

    /// <summary>Wraps ITriggerRuleStore.Upsert so a locked CSV file (antivirus scan, sync
    /// client, search indexer, or an editor with the file open) surfaces as a clear 503 instead
    /// of an unhandled UnauthorizedAccessException/IOException stack trace. CsvTriggerRuleStore
    /// already retries transient locks internally - this only fires if all retries fail.</summary>
    private bool TryUpsert(TriggerRule rule, out ActionResult? problem)
    {
        try
        {
            _ruleStore.Upsert(rule);
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            problem = Problem(
                title: "Could not save trigger to disk",
                detail: "The trigger's CSV file is locked by another process - an antivirus scan, " +
                    "a sync client (OneDrive/Dropbox), the search indexer, or an editor/Excel that " +
                    "has it open. Close anything that might have it open and try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
            return false;
        }
    }

    /// <summary>Retunes an existing trigger's lots/threshold in place - id, enabled/armed state,
    /// and fire history are left untouched, so a fired one-shot rule just re-arms with the new
    /// numbers next time it's enabled rather than needing to be deleted and recreated. Lots is
    /// converted to an actual quantity the same way Create does it.</summary>
    [HttpPut("{id}")]
    public ActionResult<TriggerRule> Update(string id, [FromBody] UpdateTriggerRuleRequest request)
    {
        var rule = _ruleStore.Get(id);
        if (rule is null) return NotFound();

        var spread = _spreadStore.Get(rule.SpreadId);
        if (spread is null)
            return BadRequest($"Spread {rule.SpreadId} no longer exists - remove this trigger instead of editing it.");

        if (request.Lots <= 0)
            return BadRequest("Lots must be greater than zero.");

        if (request.SlippagePercent is { } newSlippage && newSlippage <= 0)
            return BadRequest("SlippagePercent must be greater than zero.");

        if (request.MaxFireCount is { } newMaxFireCount && newMaxFireCount < 0)
            return BadRequest("MaxFireCount cannot be negative.");

        var lotSize = spread.NearLeg.LotSize > 0 ? spread.NearLeg.LotSize : spread.FarLeg.LotSize;
        if (lotSize <= 0)
            return BadRequest("Lot size not resolved yet for this spread - build/resolve the universe first.");

        rule.Quantity = request.Lots * lotSize;
        rule.ThresholdIsPercent = request.ThresholdIsPercent;
        if (request.SlippagePercent is { } slippage)
            rule.SlippagePercent = slippage;
        if (request.OneShot is { } oneShot)
            rule.OneShot = oneShot;
        // Deliberately does NOT reset FireCount - raising MaxFireCount on a rule that already used
        // up its shots just extends how many more it can fire; re-enable it separately via the
        // Enable/Disable action if it's currently disabled.
        if (request.MaxFireCount is { } maxFireCount)
            rule.MaxFireCount = maxFireCount;

        if (rule.Operator == TriggerOperator.AbsoluteGreaterThanOrEqual)
        {
            // Update doesn't expose a way to switch modes (same as it doesn't expose OrderType/
            // Product), so a rule created as WatchBothSides stays in that mode here - just take
            // the new magnitude, sign still ignored, same as Create.
            rule.Threshold = Math.Abs(request.Threshold);
        }
        else if (request.FireWhenGreaterOrEqual is { } explicitDirection)
        {
            // Same escape hatch as Create: an exit trigger's raw signed target (e.g. -8, waiting
            // for the spread to RISE to it) can't be represented by sign-of-Threshold inference,
            // so honor the client's explicit direction and store Threshold exactly as sent - no
            // Math.abs, no sign flip. See UpdateTriggerRuleRequest.FireWhenGreaterOrEqual and
            // TriggerList.jsx's edit modal, which sends this only for rules linked to a position.
            rule.Threshold = request.Threshold;
            rule.Operator = explicitDirection ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual;
        }
        else
        {
            rule.Threshold = request.Threshold;
            // Re-derive Operator from the (possibly just flipped) sign of the new Threshold.
            // Before this, editing e.g. +2% down to -2% left Operator stuck at
            // GreaterThanOrEqual, so the rule would silently misfire (or never fire) since the
            // comparison direction no longer matched the sign the user had just typed in.
            rule.Operator = request.Threshold >= 0 ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual;
        }

        if (!TryUpsert(rule, out var problem)) return problem!;
        return Ok(rule);
    }

    [HttpPost("{id}/disable")]
    public ActionResult<TriggerRule> Disable(string id)
    {
        var rule = _ruleStore.Get(id);
        if (rule is null) return NotFound();
        rule.Enabled = false;
        // re-persist (CsvTriggerRuleStore rewrites the file on Upsert)
        if (!TryUpsert(rule, out var problem)) return problem!;
        return Ok(rule);
    }

    /// <summary>Manually re-enables a rule - including one that disabled itself after using up
    /// every bounded shot (FireCount reached MaxFireCount - see TriggerRule.MaxFireCount) or after
    /// its single OneShot fire. FireCount is reset to 0 here (not just Enabled/IsArmed) - without
    /// this, a used-up bounded-shot rule would come back Enabled=true but still sitting at
    /// FireCount==MaxFireCount, so SpreadCalculationHostedService.EvaluateTriggersAsync's bounded-
    /// shots guard (`if (rule.FireCount >= rule.MaxFireCount) continue;`) would silently skip it
    /// forever - it would look enabled in the UI but never actually fire again. A plain OneShot
    /// rule's FireCount is always 0 already, so this is a no-op for that case.</summary>
    [HttpPost("{id}/enable")]
    public ActionResult<TriggerRule> Enable(string id)
    {
        var rule = _ruleStore.Get(id);
        if (rule is null) return NotFound();
        rule.Enabled = true;
        rule.IsArmed = true;
        rule.FireCount = 0;
        if (!TryUpsert(rule, out var problem)) return problem!;
        return Ok(rule);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) => _ruleStore.Remove(id) ? NoContent() : NotFound();

    /// <summary>Removes every trigger rule in one call - e.g. to clear out a bulk-template run
    /// before re-applying it with different parameters, instead of removing rules one by one.</summary>
    [HttpDelete]
    public ActionResult<int> DeleteAll()
    {
        var removed = _ruleStore.RemoveAll();
        return Ok(removed);
    }
}
