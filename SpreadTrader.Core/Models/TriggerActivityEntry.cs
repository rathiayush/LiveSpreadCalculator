namespace SpreadTrader.Core.Models;

/// <summary>
/// Mutable merge of a fired TriggerEvent and its (possibly not-yet-arrived) BasketExecutionEvent,
/// held in memory by ITriggerActivityStore. Previously the Trigger Activity feed existed only as
/// client-side state built up from SignalR pushes as they arrived (see hooks/spreadHub.js) - so
/// refreshing the browser (or opening a second tab) lost everything and started from an empty
/// feed, even though the fires themselves were real and already fully resolved server-side. This
/// entry mirrors exactly the shape the frontend used to synthesize itself, so GET
/// /api/triggers/activity can seed the feed the same way GET /api/spreads/snapshot already seeds
/// live spread values. Legs stays null until the matching execution arrives (rendered as
/// "placing order..." - see TriggerFeed.ExecutionStatus) - a browser that refreshes in that
/// narrow window will correctly see it as still-pending, not lose track of it having fired at
/// all. Deliberately in-memory only, not written to disk: this is a rolling activity log, not
/// configuration, and the user only asked for it to survive a page refresh, not a backend
/// restart - see InMemoryTriggerActivityStore's cap for why unbounded retention isn't needed.
/// </summary>
public sealed class TriggerActivityEntry
{
    public required Guid FireId { get; init; }
    public required string RuleId { get; init; }
    public required string SpreadId { get; init; }
    public required decimal SpreadValueAtTrigger { get; init; }
    public required string Direction { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public bool IsExitTrigger { get; init; }

    // Everything below stays at its default (Legs null, MarginBlocked false, etc.) until
    // InMemoryTriggerActivityStore.RecordExecuted attaches the matching BasketExecutionEvent.
    public IReadOnlyList<OrderLegResult>? Legs { get; set; }
    public bool MarginBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? Underlying { get; set; }
    public string? OrderKind { get; set; }
}
