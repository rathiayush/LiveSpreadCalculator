using System.Collections.Concurrent;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Tracks which rule IDs currently have a basket dispatch in flight - from the moment
/// OrderDispatchHostedService starts processing a TriggerEvent for that rule until it finishes
/// publishing the resulting BasketExecutionEvent (success, partial fill, rejection, or margin
/// block - whatever the outcome, it counts as "resolved").
///
/// Only consulted by the bounded "N sequential shots" trigger mode (TriggerRule.MaxFireCount > 0
/// - see SpreadCalculationHostedService.EvaluateTriggersAsync): a rule scheduled for several shots
/// should fire the next one immediately once the previous shot's order outcome is known, not
/// while it's still resolving. Ordinary one-shot/standing rules never check this at all.
///
/// A single in-memory ConcurrentDictionary is sufficient - there is only ever one
/// OrderDispatchHostedService instance draining one TriggerEventChannel, so "in flight" here means
/// exactly "OrderDispatchHostedService.DispatchAsync is currently running for this rule id",
/// nothing more elaborate is needed.
/// </summary>
public sealed class TriggerInFlightTracker
{
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public void MarkInFlight(string ruleId) => _inFlight[ruleId] = 0;

    public void MarkComplete(string ruleId) => _inFlight.TryRemove(ruleId, out _);

    public bool IsInFlight(string ruleId) => _inFlight.ContainsKey(ruleId);
}
