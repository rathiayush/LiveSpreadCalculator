using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Orders;
using SpreadTrader.KiteIntegration.Orders;
using SpreadTrader.KotakIntegration.Orders;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>Forwards every order placement/cancellation to whichever platform is currently active.
/// There is no in-flight-order handoff to worry about: OrderDispatchHostedService only ever calls
/// this from a trigger that was itself evaluated against the currently-active platform's ticks, so
/// an order for the "wrong" platform can't be constructed in the first place.</summary>
public sealed class SwitchingOrderGateway : IOrderGateway
{
    private readonly BrokerSwitchState _state;
    private readonly KiteOrderGateway _kite;
    private readonly DhanOrderGateway _dhan;
    private readonly KotakOrderGateway _kotak;

    public SwitchingOrderGateway(BrokerSwitchState state, KiteOrderGateway kite, DhanOrderGateway dhan, KotakOrderGateway kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IOrderGateway Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken) =>
        Active.PlaceOrderAsync(leg, cancellationToken);

    public Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken) =>
        Active.CancelOrderAsync(orderId, variety, cancellationToken);
}
