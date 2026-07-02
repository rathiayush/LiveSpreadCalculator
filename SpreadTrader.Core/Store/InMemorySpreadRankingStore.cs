using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

public sealed class InMemorySpreadRankingStore : ISpreadRankingStore
{
    private IReadOnlyList<SpreadRanking> _ranking = Array.Empty<SpreadRanking>();

    public void Set(IReadOnlyList<SpreadRanking> ranking) => _ranking = ranking;

    public IReadOnlyList<SpreadRanking> Get() => _ranking;
}
