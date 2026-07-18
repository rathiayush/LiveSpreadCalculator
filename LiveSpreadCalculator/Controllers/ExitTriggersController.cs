using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Abstractions;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Exit triggers (rules that CLOSE an existing position) - a mirror of TriggersController, backed
/// by its own IExitTriggerRuleStore/CSV file instead of the entry-trigger ITriggerRuleStore, so
/// exit rules are never mixed into the same table/list as entry rules - see
/// IExitTriggerRuleStore's doc comment. All the actual CRUD logic (Create/Update/Enable/Disable/
/// Delete/DeleteAll/GetAll) lives on TriggerRuleControllerBase and applies identically here: an
/// exit trigger fires and dispatches orders through the exact same engine path as any other rule
/// (SpreadCalculationHostedService/OrderDispatchHostedService), it's just evaluated out of a
/// separate store. No bulk-template or activity-feed endpoints here - bulk apply is entry-only
/// (see TriggersController.ApplyBulkTemplate), and the Trigger Activity feed is shared between
/// both (GET/DELETE /api/triggers/activity), not duplicated per store.
/// </summary>
[ApiController]
[Route("api/exit-triggers")]
public sealed class ExitTriggersController : TriggerRuleControllerBase
{
    public ExitTriggersController(IExitTriggerRuleStore ruleStore, ISpreadDefinitionStore spreadStore)
        : base(ruleStore, spreadStore)
    {
    }
}
