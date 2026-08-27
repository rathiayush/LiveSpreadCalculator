using LiveSpreadCalculator.Hubs;
using Microsoft.AspNetCore.SignalR;
using SpreadTrader.Core.OptionsFlow;
using SpreadTrader.OptionsFlow;

namespace LiveSpreadCalculator.Realtime;

/// <summary>
/// Pushes "optionsFlowUpdate" to connected dashboards once per refresh cycle, carrying one index's
/// whole CE/PE series.
///
/// The whole series is sent rather than a delta, deliberately. An ATM shift rebuilds the entire day
/// from the session open (see OptionsFlowEngine), so any incremental protocol would need its own
/// "discard everything and resync" path anyway - and a trading day is only a few hundred minute
/// points per side, which is small enough that always sending the full picture is simpler and can
/// never leave a client subtly out of step with the server.
/// </summary>
public sealed class SignalROptionsFlowPublisher : IOptionsFlowPublisher
{
    private readonly IHubContext<SpreadHub> _hub;

    public SignalROptionsFlowPublisher(IHubContext<SpreadHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAsync(OptionsFlowSnapshot snapshot, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("optionsFlowUpdate", snapshot, cancellationToken);
}
