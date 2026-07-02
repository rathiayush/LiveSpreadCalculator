using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Holds the latest SpreadSnapshot for every spread, regardless of whether it changed on the
/// last recalculation cycle. SpreadCalculationHostedService only pushes a "spreadUpdate" to the
/// dashboard when a value actually changes (to avoid flooding SignalR), but the 3-minute ranking
/// job and the GET /api/spreads/snapshot endpoint need the latest value for every spread even if
/// it hasn't moved recently - that's what this cache is for.
/// </summary>
public interface ISpreadSnapshotCache
{
    void Upsert(SpreadSnapshot snapshot);
    IReadOnlyList<SpreadSnapshot> GetAll();
}
