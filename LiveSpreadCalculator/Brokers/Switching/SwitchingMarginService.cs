using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Orders;
using SpreadTrader.KiteIntegration.Orders;
using SpreadTrader.KotakIntegration.Orders;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>Forwards margin checks to whichever platform is currently active.</summary>
public sealed class SwitchingMarginService : IMarginService
{
    private readonly BrokerSwitchState _state;
    private readonly KiteMarginService _kite;
    private readonly DhanMarginService _dhan;
    private readonly KotakMarginService _kotak;

    public SwitchingMarginService(BrokerSwitchState state, KiteMarginService kite, DhanMarginService dhan, KotakMarginService kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IMarginService Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken) =>
        Active.GetBasketMarginAsync(legs, cancellationToken);

    public Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default) =>
        Active.GetAvailableMarginAsync(segment, cancellationToken);
}
