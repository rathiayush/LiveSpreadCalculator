using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Pushes changed spread snapshots outward (SignalR in the Api project). Kept as an
/// abstraction so the Engine has no dependency on the transport - if the engine is later
/// split into its own process, only the implementation registered here needs to change.
/// </summary>
public interface ISpreadPublisher
{
    Task PublishAsync(IReadOnlyList<SpreadSnapshot> changed, CancellationToken cancellationToken);

    /// <summary>Pushes the full spread ranking (sorted by |SpreadPercent| descending, see
    /// SpreadRankingHostedService) to the dashboard. Fired on a much slower cadence than
    /// PublishAsync - RankingIntervalMinutes instead of every recalculation cycle.</summary>
    Task PublishRankingAsync(IReadOnlyList<SpreadRanking> ranking, CancellationToken cancellationToken);
}
