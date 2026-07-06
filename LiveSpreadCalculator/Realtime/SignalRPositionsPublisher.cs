using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

/// <summary>Broadcasts the live positions/PNL snapshot to every connected dashboard client -
/// same hub and fan-out pattern as SignalRSpreadPublisher, just a different event name so the
/// Positions & PNL page's listener doesn't have to filter spread pushes.</summary>
public sealed class SignalRPositionsPublisher : IPositionsPublisher
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalRPositionsPublisher(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAsync(IReadOnlyList<PositionSnapshot> positions, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("positionsUpdate", positions, cancellationToken);
}
