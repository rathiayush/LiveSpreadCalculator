using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

public interface ISpreadDefinitionStore
{
    void Upsert(SpreadDefinition definition);
    bool Remove(string spreadId);
    IReadOnlyList<SpreadDefinition> GetAll();
    SpreadDefinition? Get(string spreadId);
}
