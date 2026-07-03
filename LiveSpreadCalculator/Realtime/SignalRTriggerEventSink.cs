using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

public sealed class SignalRTriggerEventSink : ITriggerEventSink
{
    private readonly IHubContext<SpreadHub> _hub;
    private readonly ITriggerActivityStore _activityStore;

    public SignalRTriggerEventSink(IHubContext<SpreadHub> hub, ITriggerActivityStore activityStore)
    {
        _hub = hub;
        _activityStore = activityStore;
    }

    public Task PublishAsync(TriggerEvent triggerEvent, CancellationToken cancellationToken)
    {
        // Recorded here (not in SpreadCalculationHostedService, which only knows about
        // ITriggerEventSink) so GET /api/triggers/activity has this the instant a client would
        // otherwise have received the "triggerFired" push - see TriggerActivityEntry.
        _activityStore.RecordFired(triggerEvent);
        return _hub.Clients.All.SendAsync("triggerFired", triggerEvent, cancellationToken);
    }
}
