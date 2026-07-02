namespace SpreadTrader.Core.Models;

public readonly record struct TriggerEvent(
    string RuleId,
    string SpreadId,
    decimal SpreadValueAtTrigger,
    DateTimeOffset TriggeredAt);
