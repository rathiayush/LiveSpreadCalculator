using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

public sealed class SignalRTriggerEventSink : ITriggerEventSink
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalRTriggerEventSink(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAsync(TriggerEvent triggerEvent, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("triggerFired", triggerEvent, cancellationToken);
}
