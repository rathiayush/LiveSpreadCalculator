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
        if (_spreadStore.Get(request.SpreadId) is null)
            return BadRequest($"Unknown spread id: {request.SpreadId}");

        if (!request.AutoDirectionFromSpreadSign &&
            request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set when AutoDirectionFromSpreadSign is false - otherwise the rule would fire and place no orders.");

        if (request.Quantity <= 0)
            return BadRequest("Quantity must be greater than zero.");

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
            Quantity = request.Quantity,
            Product = request.Product,
            OrderType = request.OrderType,
            OneShot = request.OneShot,
            RearmBuffer = request.RearmBuffer,
        };

        _ruleStore.Upsert(rule);
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
        _ruleStore.Upsert(rule); // re-persist (CsvTriggerRuleStore rewrites the file on Upsert)
        return Ok(rule);
    }

    [HttpPost("{id}/enable")]
    public ActionResult<TriggerRule> Enable(string id)
    {
        var rule = _ruleStore.Get(id);
        if (rule is null) return NotFound();
        rule.Enabled = true;
        rule.IsArmed = true;
        _ruleStore.Upsert(rule);
        return Ok(rule);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) => _ruleStore.Remove(id) ? NoContent() : NotFound();
}
