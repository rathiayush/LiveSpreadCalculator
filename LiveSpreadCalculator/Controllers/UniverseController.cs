using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/universe")]
public sealed class UniverseController : ControllerBase
{
    private readonly UniverseBuilderService _builder;

    public UniverseController(UniverseBuilderService builder)
    {
        _builder = builder;
    }

    /// <summary>Asks Kite's live instrument master for every stock currently listed for NFO
    /// futures, resolves each one's near/far month contract, writes the resolved set to
    /// App_Data/universe_futures.csv, and upserts + subscribes a SpreadDefinition for every one
    /// of them - so re-running this after every monthly expiry roll (or whenever NSE adds new
    /// F&amp;O stocks) is the only thing needed to keep the whole universe streaming and
    /// configured. Pass maxSymbols to cap the build to the first N symbols (alphabetically)
    /// instead of the full exchange list - omit it (or pass 0) to build everything Kite
    /// currently lists futures for (roughly 200-220 stocks as of mid-2026 - that's the entire
    /// exchange's F&amp;O universe, not a limit in this code).</summary>
    [HttpPost("build")]
    public async Task<ActionResult<UniverseBuildResult>> Build([FromQuery] int? maxSymbols, CancellationToken cancellationToken)
    {
        if (maxSymbols is < 0)
            return BadRequest("maxSymbols must be a positive number, or omitted to build every futures-listed symbol on the exchange.");

        var result = await _builder.BuildAsync(maxSymbols, cancellationToken);
        return Ok(result);
    }
}
