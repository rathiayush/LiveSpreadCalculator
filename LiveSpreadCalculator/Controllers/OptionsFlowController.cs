using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.OptionsFlow;
using SpreadTrader.OptionsFlow;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Read-only access to the Options Flow series. Exists so the page can seed itself on load (and
/// after a browser refresh) rather than sitting blank until the next SignalR push - the same
/// snapshot-then-stream pattern the spreads page already uses.
///
/// Registered only when the feature is enabled; when it is off the endpoints below report that
/// clearly instead of 404-ing, so the dashboard can explain why the page is empty.
/// </summary>
[ApiController]
[Route("api/options-flow")]
public sealed class OptionsFlowController : ControllerBase
{
    private readonly OptionsFlowOptions _options;
    private readonly OptionsFlowEngine? _engine;

    /// <param name="engine">Null when the feature flag is off - the engine is only registered when
    /// OptionsFlow:Enabled is true.</param>
    public OptionsFlowController(IOptions<OptionsFlowOptions> options, OptionsFlowEngine? engine = null)
    {
        _options = options.Value;
        _engine = engine;
    }

    /// <summary>Whether the feature is on, and which indices it is tracking - the dashboard uses
    /// this to decide whether to show the page's nav entry at all.</summary>
    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new
    {
        enabled = _options.Enabled && _engine is not null,
        indices = _options.ResolveIndices(),
        deltaWeighted = _options.UseDeltaWeighting,
        atmShiftIntervalMinutes = _options.AtmShiftIntervalMinutes,
        strikesBelowAtm = _options.StrikesBelowAtm,
        strikesAboveAtm = _options.StrikesAboveAtm,
    });

    /// <summary>Every tracked index's latest series.</summary>
    [HttpGet("snapshot")]
    public ActionResult<IEnumerable<OptionsFlowSnapshot>> GetSnapshots()
    {
        if (_engine is null) return Ok(Array.Empty<OptionsFlowSnapshot>());
        return Ok(_engine.Snapshots.Values);
    }

    /// <summary>One index's latest series.</summary>
    [HttpGet("snapshot/{index}")]
    public ActionResult<OptionsFlowSnapshot> GetSnapshot(string index)
    {
        if (_engine is null)
            return NotFound(new { error = "Options Flow is disabled - set OptionsFlow:Enabled to true and restart." });

        var snapshot = _engine.GetSnapshot(index);
        return snapshot is null
            ? NotFound(new { error = $"No Options Flow data for '{index}' yet - it may still be resolving its strike window." })
            : Ok(snapshot);
    }
}
