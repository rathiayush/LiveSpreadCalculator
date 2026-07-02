using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Places a single order leg with the broker. Implementations are responsible for respecting
/// the broker's own rate limits - callers should not assume this returns instantly.
/// </summary>
public interface IOrderGateway
{
    Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken);
}
