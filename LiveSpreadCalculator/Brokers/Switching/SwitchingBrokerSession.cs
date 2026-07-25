using SpreadTrader.Core.Brokers;
using SpreadTrader.DhanIntegration;
using SpreadTrader.KiteIntegration;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// Forwards every call to whichever platform's own session (KiteSession or DhanSession) is
/// currently active per BrokerSwitchState. Both concrete sessions always exist and keep their own
/// state independently - switching platforms never clears either one's token, so a platform that
/// was already authenticated (e.g. Dhan via TOTP auto-renewal running quietly in the background)
/// is instantly usable the moment it becomes current, with no re-login.
/// </summary>
public sealed class SwitchingBrokerSession : IBrokerSession
{
    private readonly BrokerSwitchState _state;
    private readonly KiteSession _kite;
    private readonly DhanSession _dhan;

    public SwitchingBrokerSession(BrokerSwitchState state, KiteSession kite, DhanSession dhan)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
    }

    private IBrokerSession Active => _state.Current == BrokerPlatform.Kite ? _kite : _dhan;

    public string? AccessToken
    {
        get => Active.AccessToken;
        set => Active.AccessToken = value;
    }

    public string? ClientId => Active.ClientId;

    public bool IsAuthenticated => Active.IsAuthenticated;
}
