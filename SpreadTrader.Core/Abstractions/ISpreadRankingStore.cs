using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Holds the most recently computed spread ranking (see SpreadRankingHostedService), so a
/// dashboard client that just connected can fetch it once via GET /api/spreads/ranking instead
/// of waiting up to RankingIntervalMinutes for the next SignalR push.
/// </summary>
public interface ISpreadRankingStore
{
    void Set(IReadOnlyList<SpreadRanking> ranking);
    IReadOnlyList<SpreadRanking> Get();
}
