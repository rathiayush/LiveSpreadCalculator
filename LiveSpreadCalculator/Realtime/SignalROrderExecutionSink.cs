using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

/// <summary>Pushes BasketExecutionEvent to every connected dashboard client as "basketExecuted" -
/// the frontend matches it back to the earlier "triggerFired" push by FireId (see
/// useSpreadHub.js) so the Trigger Activity feed can show what was actually bought/sold and
/// whether it succeeded, not just that a rule crossed its threshold.</summary>
public sealed class SignalROrderExecutionSink : IOrderExecutionSink
{
    private readonly IHubContext<SpreadHub> _hub;
    private readonly ITriggerActivityStore _activityStore;

    public SignalROrderExecutionSink(IHubContext<SpreadHub> hub, ITriggerActivityStore activityStore)
    {
        _hub = hub;
        _activityStore = activityStore;
    }

    public Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        // Attaches this outcome to the matching "fired" entry recorded earlier by
        // SignalRTriggerEventSink, correlated by FireId - see TriggerActivityEntry.
        _activityStore.RecordExecuted(executionEvent);
        return _hub.Clients.All.SendAsync("basketExecuted", executionEvent, cancellationToken);
    }
}
