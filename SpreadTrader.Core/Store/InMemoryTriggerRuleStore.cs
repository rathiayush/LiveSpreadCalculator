using System.Collections.Concurrent;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

public sealed class InMemoryTriggerRuleStore : ITriggerRuleStore
{
    private readonly ConcurrentDictionary<string, TriggerRule> _rules = new();

    public void Upsert(TriggerRule rule) => _rules[rule.Id] = rule;

    public bool Remove(string ruleId) => _rules.TryRemove(ruleId, out _);

    public IReadOnlyList<TriggerRule> GetAll() => _rules.Values.ToArray();

    public IReadOnlyList<TriggerRule> GetForSpread(string spreadId) =>
        _rules.Values.Where(r => r.SpreadId == spreadId).ToArray();

    public TriggerRule? Get(string ruleId) => _rules.GetValueOrDefault(ruleId);
}
