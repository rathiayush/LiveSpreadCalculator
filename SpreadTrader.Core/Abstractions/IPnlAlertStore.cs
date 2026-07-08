using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>Mirrors ITriggerRuleStore's shape - see CsvPnlAlertStore, which follows the same
/// rewrite-whole-file-on-every-change pattern as CsvTriggerRuleStore.</summary>
public interface IPnlAlertStore
{
    void Upsert(PnlAlertRule rule);
    bool Remove(string ruleId);
    IReadOnlyList<PnlAlertRule> GetAll();
    PnlAlertRule? Get(string ruleId);
}
