using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

public interface ITriggerRuleStore
{
    void Upsert(TriggerRule rule);
    bool Remove(string ruleId);
    /// <summary>Removes every trigger rule. Returns the number of rules that were removed.</summary>
    int RemoveAll();
    IReadOnlyList<TriggerRule> GetAll();
    IReadOnlyList<TriggerRule> GetForSpread(string spreadId);
    TriggerRule? Get(string ruleId);
}
