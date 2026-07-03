using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;

namespace LiveSpreadCalculator.Realtime;

/// <summary>Pushes "kiteAuthInvalidated" to every connected dashboard the instant
/// KiteAuthFailureHandler detects a TokenException from a real Kite call - see that class for
/// why this needs to be a push rather than something the frontend only discovers by polling
/// GET /api/auth/status again later.</summary>
public sealed class SignalRKiteAuthNotifier : IKiteAuthNotifier
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalRKiteAuthNotifier(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyAuthInvalidAsync(string reason, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("kiteAuthInvalidated", new { reason }, cancellationToken);
}
