using System.Collections.Concurrent;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

public sealed class InMemorySpreadSnapshotCache : ISpreadSnapshotCache
{
    private readonly ConcurrentDictionary<string, SpreadSnapshot> _snapshots = new();

    public void Upsert(SpreadSnapshot snapshot) => _snapshots[snapshot.SpreadId] = snapshot;

    public IReadOnlyList<SpreadSnapshot> GetAll() => _snapshots.Values.ToArray();
}
