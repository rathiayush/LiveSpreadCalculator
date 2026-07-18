using Microsoft.AspNetCore.Mvc;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Entry triggers (rules that open a new position) - CRUD (Create/Update/Enable/Disable/Delete/
/// DeleteAll/GetAll) lives on the shared TriggerRuleControllerBase; this class adds what's
/// specific to entry rules only: bulk template application across many spreads, and the shared
/// Trigger Activity feed (which records fires from both this and ExitTriggersController, hence it
/// lives here rather than being duplicated). See ExitTriggersController for the exit-rule
/// counterpart, backed by its own separate store - IExitTriggerRuleStore's doc comment explains
/// why entry and exit rules are never mixed into one table.
/// </summary>
[ApiController]
[Route("api/triggers")]
public sealed class TriggersController : TriggerRuleControllerBase
{
    private readonly BulkTriggerTemplateService _bulkTemplate;
    private readonly ITriggerActivityStore _activityStore;

    public TriggersController(
        ITriggerRuleStore ruleStore,
        ISpreadDefinitionStore spreadStore,
        BulkTriggerTemplateService bulkTemplate,
        ITriggerActivityStore activityStore)
        : base(ruleStore, spreadStore)
    {
        _bulkTemplate = bulkTemplate;
        _activityStore = activityStore;
    }

    /// <summary>Seeds the Trigger Activity feed on page load (see hooks/spreadHub.js) - newest
    /// first, same in-memory history that SignalRTriggerEventSink/SignalROrderExecutionSink feed
    /// on every fire, so a browser refresh no longer loses everything since the page loaded.
    /// Cleared by a full backend restart, or explicitly via DELETE below - deliberately not
    /// persisted to disk, since this is a rolling activity log, not configuration. Covers both
    /// entry and exit trigger fires (see TriggerActivityEntry.IsExitTrigger) - the frontend splits
    /// them onto separate tabs client-side, but there's only one rolling history server-side.</summary>
    [HttpGet("activity")]
    public ActionResult<IReadOnlyList<TriggerActivityEntry>> GetActivity() =>
        Ok(_activityStore.GetRecent());

    /// <summary>Wipes the Trigger Activity feed's server-side history - see TriggerFeed's Clear
    /// button, which previously only cleared the current browser's local view (the history would
    /// reappear from GetActivity above on refresh or in another tab). Has no effect on any
    /// TriggerRule - only this rolling fired/executed log. Clears entry AND exit fires together
    /// (one shared history - see GetActivity).</summary>
    [HttpDelete("activity")]
    public IActionResult ClearActivity()
    {
        _activityStore.Clear();
        return NoContent();
    }

    /// <summary>Applies one target/quantity/product/legs template across many spreads at once
    /// (e.g. the whole built universe) instead of configuring each one by hand. Creates ONE
    /// fixed-leg rule per spread, no auto-direction - see BulkTriggerTemplateService's doc
    /// comment for why the same fixed Buy/Sell legs apply everywhere in the batch regardless of
    /// which leg is actually rich/cheap for each different underlying. Entry triggers only -
    /// there's no bulk-apply for exit triggers today.</summary>
    [HttpPost("bulk")]
    public ActionResult<BulkTriggerTemplateResult> ApplyBulkTemplate([FromBody] BulkTriggerTemplateRequest request)
    {
        if (request.NearLegAction == TriggerLegAction.None && request.FarLegAction == TriggerLegAction.None)
            return BadRequest("At least one of NearLegAction / FarLegAction must be set - otherwise the rule would fire and place no orders.");
        if (request.LotsPerTrade <= 0)
            return BadRequest("LotsPerTrade must be greater than zero.");
        if (request.SlippagePercent <= 0)
            return BadRequest("SlippagePercent must be greater than zero.");
        if (request.MaxFireCount < 0)
            return BadRequest("MaxFireCount cannot be negative.");

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
            request.SlippagePercent,
            request.MaxFireCount);

        return Ok(result);
    }
}
