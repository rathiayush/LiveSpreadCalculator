using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

public interface ITriggerRuleStore
{
    void Upsert(TriggerRule rule);
    bool Remove(string ruleId);
    IReadOnlyList<TriggerRule> GetAll();
    IReadOnlyList<TriggerRule> GetForSpread(string spreadId);
    TriggerRule? Get(string ruleId);
}
