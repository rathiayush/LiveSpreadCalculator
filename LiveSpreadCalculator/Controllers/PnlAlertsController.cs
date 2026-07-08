using Microsoft.AspNetCore.Mvc;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// CRUD for PnlAlertRule - a standing "notify me on Telegram" watch on a configured spread's
/// combined Exit PNL, evaluated by PnlAlertHostedService. Deliberately minimal: no Update endpoint
/// (delete + recreate covers editing at this scale), no per-leg alerts (see PnlAlertRule's doc
/// comment for why it's spread-scoped only).
/// </summary>
[ApiController]
[Route("api/pnl-alerts")]
public sealed class PnlAlertsController : ControllerBase
{
    private readonly IPnlAlertStore _store;
    private readonly ISpreadDefinitionStore _spreadStore;

    public PnlAlertsController(IPnlAlertStore store, ISpreadDefinitionStore spreadStore)
    {
        _store = store;
        _spreadStore = spreadStore;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<PnlAlertRule>> GetAll() => Ok(_store.GetAll());

    [HttpPost]
    public ActionResult<PnlAlertRule> Create([FromBody] CreatePnlAlertRequest request)
    {
        var spread = _spreadStore.Get(request.SpreadId);
        if (spread is null)
            return BadRequest($"No configured spread with id '{request.SpreadId}'.");

        if (_store.Get(request.Id) is not null)
            return Conflict($"A PNL alert with id '{request.Id}' already exists.");

        var rule = new PnlAlertRule
        {
            Id = request.Id,
            SpreadId = request.SpreadId,
            // Resolved server-side from the spread itself, not trusted from the client - same
            // reasoning as SquareOffPositionRequest deriving BUY/SELL from the position's own sign.
            Underlying = spread.Underlying,
            TargetExitPnl = request.TargetExitPnl,
            FireWhenGreaterOrEqual = request.FireWhenGreaterOrEqual,
            OneShot = request.OneShot,
        };

        _store.Upsert(rule);
        return Ok(rule);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) => _store.Remove(id) ? NoContent() : NotFound();
}
