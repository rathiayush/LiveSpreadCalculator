using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>Publishes the live open-positions/PNL snapshot to connected dashboard clients - see
/// LivePositionsHostedService (recomputes it on every relevant price tick and order postback)
/// and SignalRPositionsPublisher (the real-time implementation, "positionsUpdate" on the same
/// hub spreads/triggers already use).</summary>
public interface IPositionsPublisher
{
    Task PublishAsync(IReadOnlyList<PositionSnapshot> positions, CancellationToken cancellationToken);
}
