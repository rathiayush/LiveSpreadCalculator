using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Orders;
using SpreadTrader.KiteIntegration.Orders;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>Forwards margin checks to whichever platform is currently active.</summary>
public sealed class SwitchingMarginService : IMarginService
{
    private readonly BrokerSwitchState _state;
    private readonly KiteMarginService _kite;
    private readonly DhanMarginService _dhan;

    public SwitchingMarginService(BrokerSwitchState state, KiteMarginService kite, DhanMarginService dhan)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
    }

    private IMarginService Active => _state.Current == BrokerPlatform.Kite ? _kite : _dhan;

    public Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken) =>
        Active.GetBasketMarginAsync(legs, cancellationToken);

    public Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default) =>
        Active.GetAvailableMarginAsync(segment, cancellationToken);
}
