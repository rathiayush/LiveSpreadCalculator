using System.Collections.Concurrent;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

public sealed class InMemoryCdvSnapshotCache : ICdvSnapshotCache
{
    private readonly ConcurrentDictionary<string, CdvSnapshot> _byUnderlying = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(CdvSnapshot snapshot) => _byUnderlying[snapshot.Underlying] = snapshot;

    public IReadOnlyList<CdvSnapshot> Snapshot() => _byUnderlying.Values.ToArray();
}
