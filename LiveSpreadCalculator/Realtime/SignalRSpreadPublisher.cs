using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

/// <summary>
/// Broadcasts changed spread snapshots to every connected dashboard client. Batched by the
/// engine's recalculation cycle (see SpreadCalculationHostedService) rather than pushed per
/// tick, so the browser gets at most one "spreadUpdate" message per cycle (default 250ms)
/// instead of being flooded at raw tick rate.
/// </summary>
public sealed class SignalRSpreadPublisher : ISpreadPublisher
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalRSpreadPublisher(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAsync(IReadOnlyList<SpreadSnapshot> changed, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("spreadUpdate", changed, cancellationToken);

    public Task PublishRankingAsync(IReadOnlyList<SpreadRanking> ranking, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("rankingUpdate", ranking, cancellationToken);
}
