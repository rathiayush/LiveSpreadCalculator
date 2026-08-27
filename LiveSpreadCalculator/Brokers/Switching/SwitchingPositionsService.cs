using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Positions;
using SpreadTrader.KiteIntegration.Positions;
using SpreadTrader.KotakIntegration.Positions;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>Forwards position fetches to whichever platform is currently active. PositionsCache
/// (the only caller) is force-refreshed by BrokerSwitchState right after a switch, so it never
/// shows the outgoing platform's positions once the new one is live.</summary>
public sealed class SwitchingPositionsService : IPositionsService
{
    private readonly BrokerSwitchState _state;
    private readonly KitePositionsService _kite;
    private readonly DhanPositionsService _dhan;
    private readonly KotakPositionsService _kotak;

    public SwitchingPositionsService(
        BrokerSwitchState state, KitePositionsService kite, DhanPositionsService dhan, KotakPositionsService kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IPositionsService Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken) =>
        Active.GetPositionsAsync(cancellationToken);
}
