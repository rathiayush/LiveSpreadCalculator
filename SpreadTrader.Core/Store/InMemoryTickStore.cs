using System.Collections.Concurrent;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

/// <summary>
/// ConcurrentDictionary-backed tick store. One writer (the ticker ingestion loop) and many
/// readers (spread calculation timer, API reads). No locking needed beyond what the
/// dictionary already gives us.
/// </summary>
public sealed class InMemoryTickStore : ITickStore
{
    private readonly ConcurrentDictionary<uint, Tick> _ticks = new();

    public void Update(Tick tick) => _ticks[tick.InstrumentToken] = tick;

    public bool TryGet(uint instrumentToken, out Tick tick) => _ticks.TryGetValue(instrumentToken, out tick);

    public IReadOnlyCollection<Tick> Snapshot() => _ticks.Values.ToArray();

    public void Remove(uint instrumentToken) => _ticks.TryRemove(instrumentToken, out _);
}
