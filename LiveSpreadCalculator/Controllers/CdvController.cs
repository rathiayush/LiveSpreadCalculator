using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Abstractions;

namespace LiveSpreadCalculator.Controllers;

/// <summary>Read-only REST surface for the CDV feature - live values themselves only ever arrive
/// over SignalR ("cdvUpdate" on /hubs/spreads, see SignalRCdvPublisher); this just seeds a
/// dashboard client that has just connected or missed a push, the same role
/// GET /api/spreads/snapshot plays for SpreadHub.</summary>
[ApiController]
[Route("api/cdv")]
public sealed class CdvController : ControllerBase
{
    private readonly ICdvSnapshotCache _cache;

    public CdvController(ICdvSnapshotCache cache)
    {
        _cache = cache;
    }

    [HttpGet("snapshot")]
    public ActionResult<object> GetSnapshot() => Ok(_cache.Snapshot());
}
