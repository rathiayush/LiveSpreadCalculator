using Microsoft.AspNetCore.Mvc;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/triggers")]
public sealed class TriggersController : ControllerBase
{
    private readonly ITriggerRuleStore _ruleStore;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly BulkTriggerTemplateService _bulkTemplate;
    private readonly ITriggerActivityStore _activityStore;

    public TriggersController(
        ITriggerRuleStore ruleStore,
        ISpreadDefinitionStore spreadStore,
        BulkTriggerTemplateService bulkTemplate,
        ITriggerActivityStore activityStore)
    {
        _ruleStore = ruleStore;
        _spreadStore = spreadStore;
        _bulkTemplate = bulkTemplate;
        _activityStore = activityStore;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<TriggerRule>> GetAll([FromQuery] string? spreadId) =>
        Ok(string.IsNullOrEmpty(spreadId) ? _ruleStore.GetAll() : _ruleStore.GetForSpread(spreadId));

    /// <summary>Seeds the Trigger Activity feed on page load (see hooks/spreadHub.js) - newest
    /// first, same in-memory history that SignalRTriggerEventSink/SignalROrderExecutionSink feed
    /// on every fire, so a browser refresh no longer loses everything since the page loaded.
    /// Cleared by a full backend restart, or explicitly via DELETE below - deliberately not
    /// persisted to disk, since this is a rolling activity log, not configuration.</summary>
    [HttpGet("activity")]
    public ActionResult<IReadOnlyList<TriggerActivityEntry>> GetActivity() =>
        Ok(_activityStore.GetRecent());

    /// <summary>Wipes the Trigger Activity feed's server-side history - see TriggerFeed's Clear
    /// button, which previously only cleared the current browser's local view (the history would
    /// reappear from GetActivity above on refresh or in another tab). Has no effect on any
    /// TriggerRule - only this rolling fired/executed log.</summary>
    [HttpDelete("activity")]
    public IActionResult ClearActivity()
    {
        _activityStore.Clear();
        return NoContent();
    }

    [HttpPost]
    public ActionResult<TriggerRule> Create([FromBody] CreateTriggerRuleRequest request)
    {
        var spread = _spreadStore.Get(request.SpreadId);
        if (spread is null)
            return BadRequest($"Unknown spread id: {request.SpreadId}");

        if (!request.AutoDirectionFromSpreadSign &&
            request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set when AutoDirectionFromSpreadSign is false - otherwise the rule would fire and place no orders.");

        // WatchBothSides fires on whichever side of zero the spread happens to cross, so a fixed
        // manual leg action would be wrong exactly half the time (it can't flip to match
        // whichever side actually triggered) - only auto-direction, which decides the trade live
        // from the real direction at fire time, makes sense here.
        if (request.WatchBothSides && !request.AutoDirectionFromSpreadSign)
            return BadRequest("WatchBothSides requires AutoDirectionFromSpreadSign - a fixed manual leg action can't correctly handle a deviation that might fire from either side.");

        if (request.WatchBothSides && request.Threshold == 0)
            return BadRequest("Threshold must be non-zero when WatchBothSides is true.");

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
            // WatchBothSides -> AbsoluteGreaterThanOrEqual with Threshold forced positive (its
            // sign is meaningless in this mode - see CreateTriggerRuleRequest.WatchBothSides).
            // Otherwise: FireWhenGreaterOrEqual, if the client sent one, wins outright - it exists
            // specifically because sign-inference can't represent every valid rule (a negative
            // target you're waiting for the spread to RISE to, e.g. an exit rule recovering from
            // -15 toward -7 - see CreateTriggerRuleRequest.FireWhenGreaterOrEqual). Falls back to
            // the original sign-of-Threshold inference when it's null, for every existing caller
            // (TriggerForm.jsx's ordinary Add Trigger page) that never sends it.
            Operator = request.WatchBothSides
                ? TriggerOperator.AbsoluteGreaterThanOrEqual
                : request.FireWhenGreaterOrEqual is { } explicitDirection
                    ? (explicitDirection ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual)
                    : request.Threshold >= 0 ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual,
            Threshold = request.WatchBothSides ? Math.Abs(request.Threshold) : request.Threshold,
            ThresholdIsPercent = request.ThresholdIsPercent,
            NearLegAction = request.NearLegAction,
            FarLegAction = request.FarLegAction,
            AutoDirectionFromSpreadSign = request.AutoDirectionFromSpreadSign,
            Quantity = request.Lots * lotSize,
            Product = request.Product,
            OrderType = request.OrderType,
            OneShot = request.OneShot,
            RearmBuffer = request.RearmBuffer,
            MaxFireCount = request.MaxFireCount,
            SlippagePercent = request.SlippagePercent,
            // Only set for exit rules created from the Positions & PNL page - see
            // TriggerRule.LinkedPositionTradingSymbol and PositionsController.SquareOff, which
            // deletes any rule matching these three fields the moment that position is manually
            // squared off.
            LinkedPositionTradingSymbol = request.LinkedPositionTradingSymbol,
            LinkedPositionExchange = request.LinkedPositionExchange,
            LinkedPositionProduct = request.LinkedPositionProduct,
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
                detail: "The trigger's CSV file (App_Data/triggers.csv) is locked by another process " +
                    "- an antivirus scan, a sync client (OneDrive/Dropbox), the search indexer, or " +
                    "an editor/Excel that has it open. Close anything that might have it open and try again.",
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
        // up its shots (and disabled itself as a result) just extends how many more it can fire;
        // re-enable it separately via the Enable/Disable action if it's currently disabled.
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

    /// <summary>Applies one target/quantity/product/legs template across many spreads at once
    /// (e.g. the whole built universe) instead of configuring each one by hand. Creates ONE
    /// fixed-leg rule per spread, no auto-direction - see BulkTriggerTemplateService's doc
    /// comment for why the same fixed Buy/Sell legs apply everywhere in the batch regardless of
    /// which leg is actually rich/cheap for each different underlying.</summary>
    [HttpPost("bulk")]
    public ActionResult<BulkTriggerTemplateResult> ApplyBulkTemplate([FromBody] BulkTriggerTemplateRequest request)
    {
        if (request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set - otherwise the rule would fire and place no orders.");
        if (request.LotsPerTrade <= 0)
            return BadRequest("LotsPerTrade must be greater than zero.");
        if (request.SlippagePercent <= 0)
            return BadRequest("SlippagePercent must be greater than zero.");

        var result = _bulkTemplate.Apply(
            request.SpreadIds,
            request.TargetPercent,
            request.FireWhenGreaterOrEqual,
            request.NearLegAction,
            request.FarLegAction,
            request.LotsPerTrade,
            request.Product,
            request.OrderType,
            request.OneShot,
            request.RearmBufferPercent,
            request.SlippagePercent);

        return Ok(result);
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

    [HttpPost("{id}/enable")]
    public ActionResult<TriggerRule> Enable(string id)
    {
        var rule = _ruleStore.Get(id);
        if (rule is null) return NotFound();
        rule.Enabled = true;
        rule.IsArmed = true;
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
