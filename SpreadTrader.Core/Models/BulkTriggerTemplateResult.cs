namespace SpreadTrader.Core.Models;

/// <summary>Result of applying one trigger template across many spreads at once - see
/// BulkTriggerTemplateService. Two TriggerRules are created per spread (one for each direction
/// of the spread % deviation), both with AutoDirectionFromSpreadSign so the actual buy/sell legs
/// are resolved from the sign of the spread at fire time.</summary>
public sealed record BulkTriggerTemplateResult(
    int SpreadCount,
    int RulesCreated,
    IReadOnlyList<string> Skipped);
