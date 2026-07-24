using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/universe")]
public sealed class UniverseController : ControllerBase
{
    private readonly UniverseBuilderService _builder;
    private readonly McxOptions _mcx;

    public UniverseController(UniverseBuilderService builder, McxOptions mcx)
    {
        _builder = builder;
        _mcx = mcx;
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

    /// <summary>MCX commodity counterpart to build. Asks the live MCX instrument master for the
    /// configured commodity underlyings (silver, gold, crude oil, natural gas and their mini/micro
    /// variants - see McxOptions.Underlyings) and upserts + subscribes two kinds of arbitrage
    /// spread: near/far calendar spreads and same-expiry (or closest-expiry, for cross-contract)
    /// spreads. Returns 409 Conflict when the MCX add-on is switched off (Mcx:Enabled=false) so
    /// the feature is a pure config flag with no effect on the NFO engine until turned on.</summary>
    [HttpPost("build-mcx")]
    public async Task<ActionResult<UniverseBuildResult>> BuildMcx(CancellationToken cancellationToken)
    {
        if (!_mcx.Enabled)
            return Conflict("The MCX commodity add-on is disabled. Set Mcx:Enabled=true (appsettings or Mcx__Enabled env var) and restart to use it.");

        var result = await _builder.BuildMcxAsync(cancellationToken);
        return Ok(result);
    }
}
