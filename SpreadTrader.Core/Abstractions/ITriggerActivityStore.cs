using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>In-memory rolling history of fired triggers and their execution outcomes, so GET
/// /api/triggers/activity can reseed the Trigger Activity feed after a browser refresh - see
/// TriggerActivityEntry. Recorded from the same two places that already push to SignalR
/// (SignalRTriggerEventSink / SignalROrderExecutionSink), not from the hosted services directly -
/// see those classes' doc comments.</summary>
public interface ITriggerActivityStore
{
    void RecordFired(TriggerEvent triggerEvent);
    void RecordExecuted(BasketExecutionEvent executionEvent);
    IReadOnlyList<TriggerActivityEntry> GetRecent();
}
