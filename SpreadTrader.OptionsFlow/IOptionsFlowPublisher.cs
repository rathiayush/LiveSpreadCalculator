using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.OptionsFlow;

/// <summary>
/// Pushes a refreshed Options Flow snapshot to connected dashboards. Mirrors the existing
/// ISpreadPublisher/IPositionsPublisher pattern: this project stays free of any ASP.NET or SignalR
/// dependency, and the web project supplies the real implementation.
/// </summary>
public interface IOptionsFlowPublisher
{
    Task PublishAsync(OptionsFlowSnapshot snapshot, CancellationToken cancellationToken);
}
