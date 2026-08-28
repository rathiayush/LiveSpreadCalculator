using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Pushes a changed CDV snapshot outward (SignalR, in the Api project) - the CDV counterpart of
/// ISpreadPublisher. Kept as an abstraction for the same reason: SpreadTrader.Engine has no
/// dependency on the transport.
/// </summary>
public interface ICdvPublisher
{
    Task PublishAsync(CdvSnapshot snapshot, CancellationToken cancellationToken);
}
