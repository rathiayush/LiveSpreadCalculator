using Microsoft.AspNetCore.Mvc;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/spreads")]
public sealed class SpreadsController : ControllerBase
{
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly TickIngestionHostedService _tickIngestion;
    private readonly ISpreadSnapshotCache _snapshotCache;
    private readonly ISpreadRankingStore _rankingStore;

    public SpreadsController(
        ISpreadDefinitionStore spreadStore,
        TickIngestionHostedService tickIngestion,
        ISpreadSnapshotCache snapshotCache,
        ISpreadRankingStore rankingStore)
    {
        _spreadStore = spreadStore;
        _tickIngestion = tickIngestion;
        _snapshotCache = snapshotCache;
        _rankingStore = rankingStore;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<SpreadDefinition>> GetAll() => Ok(_spreadStore.GetAll());

    /// <summary>The latest known value for every spread, regardless of when it last changed.
    /// Used by the dashboard on first load / reconnect so it isn't empty until the next
    /// "spreadUpdate" push arrives - after that, SignalR keeps it live.</summary>
    [HttpGet("snapshot")]
    public ActionResult<IReadOnlyList<SpreadSnapshot>> GetSnapshot() => Ok(_snapshotCache.GetAll());

    /// <summary>The most recent 3-minute (configurable via Engine:RankingIntervalMinutes)
    /// ranking of every spread by absolute spread percentage, biggest first.</summary>
    [HttpGet("ranking")]
    public ActionResult<IReadOnlyList<SpreadRanking>> GetRanking() => Ok(_rankingStore.Get());

    /// <summary>Creates (or replaces) a spread definition and immediately resolves its legs to
    /// Kite instrument tokens and subscribes the live ticker - no restart required.</summary>
    [HttpPost]
    public async Task<ActionResult<SpreadDefinition>> Create([FromBody] CreateSpreadRequest request, CancellationToken cancellationToken)
    {
        var spread = new SpreadDefinition
        {
            Id = request.Id,
            Underlying = request.Underlying,
            NearLeg = new InstrumentConfig { TradingSymbol = request.NearLeg.TradingSymbol, Exchange = request.NearLeg.Exchange },
            FarLeg = new InstrumentConfig { TradingSymbol = request.FarLeg.TradingSymbol, Exchange = request.FarLeg.Exchange },
        };

        _spreadStore.Upsert(spread);
        await _tickIngestion.ResolveAndSubscribeAsync(spread, cancellationToken);

        return Ok(spread);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) => _spreadStore.Remove(id) ? NoContent() : NotFound();
}
