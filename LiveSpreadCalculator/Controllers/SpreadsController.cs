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
    private readonly ISpreadHistoryStore _historyStore;

    public SpreadsController(
        ISpreadDefinitionStore spreadStore,
        TickIngestionHostedService tickIngestion,
        ISpreadSnapshotCache snapshotCache,
        ISpreadRankingStore rankingStore,
        ISpreadHistoryStore historyStore)
    {
        _spreadStore = spreadStore;
        _tickIngestion = tickIngestion;
        _snapshotCache = snapshotCache;
        _rankingStore = rankingStore;
        _historyStore = historyStore;
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

    /// <summary>The stored hourly spread history for one spread, oldest-first - this is what the
    /// "Live Spread" popup plots as a datetime-vs-spread chart. Defaults to the last 30 days;
    /// override with ISO-8601 from/to query params (e.g. ?from=2026-07-01T00:00:00Z).</summary>
    [HttpGet("{id}/history")]
    public async Task<ActionResult<IReadOnlyList<SpreadHistoryPoint>>> GetHistory(
        string id,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var fromUtc = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var points = await _historyStore.GetHistoryAsync(id, fromUtc, to, cancellationToken);
        return Ok(points);
    }

    /// <summary>Manually flush all stored history for one spread (the automatic per-leg-expiry flush
    /// runs daily inside the capture service - see SpreadHistoryCaptureHostedService).</summary>
    [HttpDelete("{id}/history")]
    public async Task<IActionResult> DeleteHistory(string id, CancellationToken cancellationToken)
    {
        var removed = await _historyStore.DeleteBySpreadIdAsync(id, cancellationToken);
        return Ok(new { removed });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var removed = _spreadStore.Remove(id);
        // Also drop it from the snapshot cache - otherwise a deleted spread keeps appearing in
        // GET /api/spreads/snapshot and in the ranking forever, since nothing else ever prunes
        // that cache (see ISpreadSnapshotCache.Remove).
        _snapshotCache.Remove(id);
        return removed ? NoContent() : NotFound();
    }
}
