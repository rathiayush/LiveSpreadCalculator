namespace SpreadTrader.Core.Models;

/// <summary>Result of applying one trigger template across many spreads at once - see
/// BulkTriggerTemplateService. One TriggerRule is created per spread, with a fixed signed target
/// %, explicit comparison, and fixed Buy/Sell/None leg actions applied identically across the
/// whole batch (see BulkTriggerTemplateRequest) - no auto-direction, since a single template
/// can't know in advance which leg is actually rich/cheap for each different underlying.</summary>
public sealed record BulkTriggerTemplateResult(
    int SpreadCount,
    int RulesCreated,
    IReadOnlyList<string> Skipped);
