using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

/// <summary>
/// Keeps the most recent fired triggers (and their execution outcomes once they arrive) in
/// memory, newest first, so the Trigger Activity feed survives a browser refresh - see
/// TriggerActivityEntry for why this exists and why it's deliberately not written to disk.
/// </summary>
public sealed class InMemoryTriggerActivityStore : ITriggerActivityStore
{
    // Matches (and slightly exceeds) the cap the frontend already applied client-side before
    // this store existed - bounds memory across a long-running session with no restart, while
    // still comfortably holding a full trading day's worth of fires for practical purposes.
    private const int MaxEntries = 200;

    private readonly object _lock = new();
    private readonly LinkedList<TriggerActivityEntry> _entries = new(); // newest first

    public void RecordFired(TriggerEvent triggerEvent)
    {
        lock (_lock)
        {
            _entries.AddFirst(new TriggerActivityEntry
            {
                FireId = triggerEvent.FireId,
                RuleId = triggerEvent.RuleId,
                SpreadId = triggerEvent.SpreadId,
                SpreadValueAtTrigger = triggerEvent.SpreadValueAtTrigger,
                Direction = triggerEvent.Direction,
                TriggeredAt = triggerEvent.TriggeredAt,
                IsExitTrigger = triggerEvent.IsExitTrigger,
            });

            while (_entries.Count > MaxEntries)
                _entries.RemoveLast();
        }
    }

    public void RecordExecuted(BasketExecutionEvent executionEvent)
    {
        lock (_lock)
        {
            // Won't be found if the matching "fired" entry already scrolled off the MaxEntries
            // cap (only plausible with a huge fire burst between the two events) - nothing
            // useful to attach it to in that case, so it's silently dropped rather than
            // re-inserted out of order.
            var entry = _entries.FirstOrDefault(e => e.FireId == executionEvent.FireId);
            if (entry is null) return;

            entry.Legs = executionEvent.Legs;
            entry.MarginBlocked = executionEvent.MarginBlocked;
            entry.BlockedReason = executionEvent.BlockedReason;
            entry.ExecutedAt = executionEvent.ExecutedAt;
            entry.Underlying = executionEvent.Underlying;
            entry.OrderKind = executionEvent.OrderKind;
        }
    }

    public IReadOnlyList<TriggerActivityEntry> GetRecent()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
