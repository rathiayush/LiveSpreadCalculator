using SpreadTrader.Core.Brokers;
using SpreadTrader.DhanIntegration.Auth;
using SpreadTrader.KiteIntegration.Auth;
using SpreadTrader.KotakIntegration.Auth;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// Forwards every call to whichever platform's auth service is currently active. Login/callback
/// calls only ever make sense against the CURRENTLY active platform - there is no way to complete
/// a Kite browser login while Dhan or Kotak is active, for instance, since the dashboard only ever
/// shows one broker's login UI (whichever GetBroker reports as current). Switching TO Kite first,
/// then logging in if its daily token has expired, is the expected flow - see
/// BrokerPlatformPage.jsx. Kotak specifically has no browser flow at all
/// (SupportsInteractiveLogin is always false - see KotakAuthService) and is never offered in that
/// UI anyway - see BrokerRegistration's doc comment on why.
/// </summary>
public sealed class SwitchingBrokerAuthService : IBrokerAuthService
{
    private readonly BrokerSwitchState _state;
    private readonly KiteAuthService _kite;
    private readonly DhanAuthService _dhan;
    private readonly KotakAuthService _kotak;

    public SwitchingBrokerAuthService(BrokerSwitchState state, KiteAuthService kite, DhanAuthService dhan, KotakAuthService kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IBrokerAuthService Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public string PlatformName => Active.PlatformName;
    public bool SupportsInteractiveLogin => Active.SupportsInteractiveLogin;
    public bool SupportsManualToken => Active.SupportsManualToken;
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
