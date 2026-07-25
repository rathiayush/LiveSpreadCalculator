using SpreadTrader.Core.Brokers;
using SpreadTrader.DhanIntegration.Auth;
using SpreadTrader.KiteIntegration.Auth;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// Forwards every call to whichever platform's auth service is currently active. Login/callback
/// calls only ever make sense against the CURRENTLY active platform - there is no way to complete
/// a Kite browser login while Dhan is active, for instance, since the dashboard only ever shows
/// one broker's login UI (whichever GetBroker reports as current). Switching TO Kite first, then
/// logging in if its daily token has expired, is the expected flow - see BrokerPlatformPage.jsx.
/// </summary>
public sealed class SwitchingBrokerAuthService : IBrokerAuthService
{
    private readonly BrokerSwitchState _state;
    private readonly KiteAuthService _kite;
    private readonly DhanAuthService _dhan;

    public SwitchingBrokerAuthService(BrokerSwitchState state, KiteAuthService kite, DhanAuthService dhan)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
    }

    private IBrokerAuthService Active => _state.Current == BrokerPlatform.Kite ? _kite : _dhan;

    public string PlatformName => Active.PlatformName;
    public bool SupportsInteractiveLogin => Active.SupportsInteractiveLogin;
    public string NotAuthenticatedGuidance => Active.NotAuthenticatedGuidance;

    public Task<string> BuildLoginUrlAsync(CancellationToken cancellationToken) =>
        Active.BuildLoginUrlAsync(cancellationToken);

    public Task<BrokerLoginResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string?> callbackQuery, CancellationToken cancellationToken) =>
        Active.CompleteLoginAsync(callbackQuery, cancellationToken);

    public Task<bool> VerifyAccessTokenAsync(string accessToken, CancellationToken cancellationToken) =>
        Active.VerifyAccessTokenAsync(accessToken, cancellationToken);

    public DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc) =>
        Active.GetTokenExpiryUtc(issuedAtUtc);
}
