using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.Core.Store;

/// <summary>
/// Nothing more than CsvTriggerRuleStore pointed at its own CSV file (Storage:ExitTriggersCsvPath,
/// default App_Data/exit_triggers.csv) and registered under IExitTriggerRuleStore instead of
/// ITriggerRuleStore - see that interface's doc comment for why exit rules get a fully separate
/// store rather than a shared one with a type flag. Every read/write/CSV-format concern lives in
/// the base class; this only exists to give DI two distinct singletons of otherwise-identical
/// behavior.
/// </summary>
public sealed class CsvExitTriggerRuleStore : CsvTriggerRuleStore, IExitTriggerRuleStore
{
    public CsvExitTriggerRuleStore(string filePath, ILogger<CsvTriggerRuleStore>? logger = null)
        : base(filePath, logger)
    {
    }
}
