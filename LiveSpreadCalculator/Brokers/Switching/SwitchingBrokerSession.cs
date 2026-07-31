using SpreadTrader.Core.Brokers;
using SpreadTrader.DhanIntegration;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KotakIntegration;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// Forwards every call to whichever platform's own session (KiteSession/DhanSession/KotakSession)
/// is currently active per BrokerSwitchState. Every concrete session always exists and keeps its
/// own state independently - switching platforms never clears any of their tokens, so a platform
/// that was already authenticated (e.g. Dhan via TOTP auto-renewal, or Kotak via its own
/// auto-renewal, running quietly in the background) is instantly usable the moment it becomes
/// current, with no re-login.
/// </summary>
public sealed class SwitchingBrokerSession : IBrokerSession
{
    private readonly BrokerSwitchState _state;
    private readonly KiteSession _kite;
    private readonly DhanSession _dhan;
    private readonly KotakSession _kotak;

    public SwitchingBrokerSession(BrokerSwitchState state, KiteSession kite, DhanSession dhan, KotakSession kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IBrokerSession Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public string? AccessToken
    {
        get => Active.AccessToken;
        set => Active.AccessToken = value;
    }

    public string? ClientId => Active.ClientId;

    public bool IsAuthenticated => Active.IsAuthenticated;
}
