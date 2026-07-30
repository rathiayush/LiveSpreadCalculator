using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Durable store of the hourly spread snapshots (see SpreadHistoryPoint). Unlike ISpreadSnapshotCache
/// (in-memory, latest-value-only, lost on restart), this is a real database so the "Live Spread"
/// popup can plot a datetime-vs-spread history and so a leg's rows can be flushed when it expires.
///
/// Implemented by EfSpreadHistoryStore (PostgreSQL / Npgsql). All methods are async since they hit
/// the database; the capture path uses AddBatchAsync (a single bulk insert per hourly cycle) rather
/// than a row-at-a-time insert.
/// </summary>
public interface ISpreadHistoryStore
{
    /// <summary>Creates the database and table if they don't exist yet. Called once at startup
    /// (see Program.cs) - same "the store provisions its own backing storage on first use" pattern
    /// as the CSV stores creating their file in Load().</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a whole hourly capture in one bulk insert. No-op for an empty batch.</summary>
    Task AddBatchAsync(IReadOnlyList<SpreadHistoryPoint> points, CancellationToken cancellationToken = default);

    /// <summary>All stored points for one spread within [fromUtc, toUtc] (nulls = unbounded),
    /// oldest first - exactly the shape the chart plots. Returns an empty list for an unknown id.</summary>
    Task<IReadOnlyList<SpreadHistoryPoint>> GetHistoryAsync(
        string spreadId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every stored point for one spread id. Returns rows removed.</summary>
    Task<int> DeleteBySpreadIdAsync(string spreadId, CancellationToken cancellationToken = default);

    /// <summary>Flushes rows whose NEAR leg has expired on or before <paramref name="asOf"/> - i.e.
    /// the leg the spread was tracking is gone, so its history is stale and the engine has (or will)
    /// roll to the next contracts. Returns rows removed. This is the monthly "when a leg expires,
    /// flush that leg's data" job (see SpreadHistoryCaptureHostedService).</summary>
    Task<int> PurgeExpiredAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>Deletes rows captured strictly before <paramref name="cutoffUtc"/> - the age-based
    /// retention cap run by the daily maintenance job (see DailyMaintenanceHostedService), a
    /// belt-and-suspenders bound on table size independent of the per-leg expiry flush. Returns rows
    /// removed.</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}
