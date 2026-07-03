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

    public SignalROrderExecutionSink(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("basketExecuted", executionEvent, cancellationToken);
}
