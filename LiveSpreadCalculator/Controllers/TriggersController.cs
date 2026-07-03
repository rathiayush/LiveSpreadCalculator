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

    public TriggersController(ITriggerRuleStore ruleStore, ISpreadDefinitionStore spreadStore, BulkTriggerTemplateService bulkTemplate)
    {
        _ruleStore = ruleStore;
        _spreadStore = spreadStore;
        _bulkTemplate = bulkTemplate;
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

        if (!request.AutoDirectionFromSpreadSign &&
            request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set when AutoDirectionFromSpreadSign is false - otherwise the rule would fire and place no orders.");

        if (request.Lots <= 0)
            return BadRequest("Lots must be greater than zero.");

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
            Operator = request.Operator,
            Threshold = request.Threshold,
            ThresholdIsPercent = request.ThresholdIsPercent,
            NearLegAction = request.NearLegAction,
            FarLegAction = request.FarLegAction,
            AutoDirectionFromSpreadSign = request.AutoDirectionFromSpreadSign,
            Quantity = request.Lots * lotSize,
            Product = request.Product,
            OrderType = request.OrderType,
            OneShot = request.OneShot,
            RearmBuffer = request.RearmBuffer,
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

        var lotSize = spread.NearLeg.LotSize > 0 ? spread.NearLeg.LotSize : spread.FarLeg.LotSize;
        if (lotSize <= 0)
            return BadRequest("Lot size not resolved yet for this spread - build/resolve the universe first.");

        rule.Quantity = request.Lots * lotSize;
        rule.Threshold = request.Threshold;
        rule.ThresholdIsPercent = request.ThresholdIsPercent;

        if (!TryUpsert(rule, out var problem)) return problem!;
        return Ok(rule);
    }

    /// <summary>Applies one threshold/quantity/product template across many spreads at once
    /// (e.g. the whole built universe) instead of configuring each one by hand. Creates two
    /// percent-threshold, auto-direction rules per spread - see BulkTriggerTemplateService.
    /// </summary>
    [HttpPost("bulk")]
    public ActionResult<BulkTriggerTemplateResult> ApplyBulkTemplate([FromBody] BulkTriggerTemplateRequest request)
    {
        if (request.ThresholdPercent <= 0)
            return BadRequest("ThresholdPercent must be greater than zero.");
        if (request.LotsPerTrade <= 0)
            return BadRequest("LotsPerTrade must be greater than zero.");

        var result = _bulkTemplate.Apply(
            request.SpreadIds,
            request.ThresholdPercent,
            request.LotsPerTrade,
            request.Product,
            request.OrderType,
            request.OneShot,
            request.RearmBufferPercent);

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
