using System.Collections.Concurrent;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

public sealed class InMemorySpreadDefinitionStore : ISpreadDefinitionStore
{
    private readonly ConcurrentDictionary<string, SpreadDefinition> _definitions = new();

    public void Upsert(SpreadDefinition definition) => _definitions[definition.Id] = definition;

    public bool Remove(string spreadId) => _definitions.TryRemove(spreadId, out _);

    public IReadOnlyList<SpreadDefinition> GetAll() => _definitions.Values.ToArray();

    public SpreadDefinition? Get(string spreadId) => _definitions.GetValueOrDefault(spreadId);
}
