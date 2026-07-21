namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Same shape as ITriggerRuleStore, and evaluated by the same engine loop
/// (SpreadCalculationHostedService), but registered as a SEPARATE store/CSV file so exit rules
/// (rules meant to CLOSE an existing position) are never mixed into the same table/list as entry
/// rules (rules that open a new position) - not even tagged-and-filtered within one shared store,
/// but physically distinct storage from the ground up. A marker interface rather than a second
/// copy of the member list, so both stores stay structurally identical and any future change to
/// ITriggerRuleStore's shape applies to both without separate upkeep - see
/// CsvExitTriggerRuleStore, which is nothing more than CsvTriggerRuleStore pointed at a different
/// file.
/// </summary>
public interface IExitTriggerRuleStore : ITriggerRuleStore
{
}
