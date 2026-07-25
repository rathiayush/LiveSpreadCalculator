using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;

namespace LiveSpreadCalculator.Realtime;

/// <summary>Pushes "brokerAuthInvalidated" to every connected dashboard the instant the active
/// broker's auth-failure handler detects a dead access token from a real API call (Kite's
/// TokenException, Dhan's DH-901 or an 807 feed disconnect) - see those classes for why this needs
/// to be a push rather than something the frontend only discovers by polling GET /api/auth/status
/// again later.</summary>
public sealed class SignalRBrokerAuthNotifier : IBrokerAuthNotifier
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalRBrokerAuthNotifier(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyAuthInvalidAsync(string reason, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("brokerAuthInvalidated", new { reason }, cancellationToken);
}
