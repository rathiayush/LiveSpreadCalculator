using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Places a single order leg with the broker. Implementations are responsible for respecting
/// the broker's own rate limits - callers should not assume this returns instantly.
/// </summary>
public interface IOrderGateway
{
    Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken);

    /// <summary>Cancels an order that's still open/pending (e.g. a DAY LIMIT leg whose fill
    /// status couldn't be confirmed as terminal within OrderDispatchHostedService's short poll
    /// window) so it doesn't sit resting and unmanaged, potentially filling later and creating a
    /// naked position with no record of it ever being handled. Returns true if Kite accepted the
    /// cancel request - false (logged, not thrown) on failure, since a failed cancel attempt
    /// shouldn't take down the rest of dispatch/square-off.</summary>
    Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken);
}
