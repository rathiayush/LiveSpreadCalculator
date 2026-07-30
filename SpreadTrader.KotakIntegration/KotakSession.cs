using SpreadTrader.Core.Brokers;

namespace SpreadTrader.KotakIntegration;

/// <summary>
/// In-memory holder for the live Kotak Neo session. Unlike Dhan there is no portal-generated
/// long-lived token to seed from at construction - Kotak's session only ever comes from completing
/// the two-step TOTP+MPIN login (see KotakAuthService), so this starts empty and
/// KotakSessionAutoRenewalHostedService logs in within seconds of boot, the same promise Dhan's
/// TOTP mode makes.
///
/// Kotak's login response carries FOUR values every subsequent call needs, not just one bearer
/// token - see KotakAuthService.GenerateAccessTokenViaTotpLoginAsync:
///  - <see cref="AccessToken"/> is Kotak's "edit token" (<c>Auth</c> header on every REST call).
///  - <see cref="Sid"/> is the paired session id (<c>Sid</c> header, sent alongside Auth on every
///    call - Kotak's REST API is meaningless with only one of the two).
///  - <see cref="BaseUrl"/> is the actual REST host to use for every call THIS session makes -
///    confirmed by Kotak's own trade-api-guide ("Save your TRADING_TOKEN, TRADING_SID, and
///    BASE_URL for all subsequent API calls"), so it is the login response's own answer to the
///    question KotakOptions.RestBaseUrl otherwise has to guess at. See KotakApiClient, which uses
///    this in preference to the configured default whenever it's set.
///  - <see cref="ClientId"/> is the UCC (client code) the session belongs to - matches
///    IBrokerSession's contract (Dhan's dhanClientId-equivalent), used where an adapter needs to
///    know which account it's talking to rather than just proving it's authenticated.
/// </summary>
public sealed class KotakSession : IBrokerSession
{
    private readonly KotakOptions _options;

    public KotakSession(Microsoft.Extensions.Options.IOptions<KotakOptions> options)
    {
        _options = options.Value;
        ClientId = string.IsNullOrWhiteSpace(_options.Ucc) ? null : _options.Ucc;
    }

    /// <summary>Kotak's "edit token" from the totp_validate step - the <c>Auth</c> header value on
    /// every authenticated REST call and the WebSocket auth handshake.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Kotak's "edit sid" from the totp_validate step - the <c>Sid</c> header value that
    /// must accompany <see cref="AccessToken"/> on every authenticated call. Null whenever
    /// AccessToken is null; the two are always set/cleared together (see ClearSession).</summary>
    public string? Sid { get; set; }

    /// <summary>The REST host the totp_validate response told this session to use, if it returned
    /// one - see the class doc comment. Null until a successful login; KotakApiClient falls back
    /// to KotakOptions.RestBaseUrl's configured guess whenever this is null (e.g. before the first
    /// login completes, or if a given response happens not to include it).</summary>
    public string? BaseUrl { get; set; }

    public string? ClientId { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(Sid);

    /// <summary>Raised by <see cref="ClearSession"/> - the one signal
    /// KotakSessionAutoRenewalHostedService needs to react to a dead session immediately instead of
    /// waiting for its next scheduled renewal. Mirrors DhanSession.SessionInvalidated exactly.</summary>
    public event Action? SessionInvalidated;

    /// <summary>The one place a dead session is recorded - see KotakAuthFailureHandler, the only
    /// caller. Clears both token fields together (a lone Auth or Sid is meaningless to Kotak) and
    /// wakes the auto-renewal loop.</summary>
    public void ClearSession()
    {
        AccessToken = null;
        Sid = null;
        SessionInvalidated?.Invoke();
    }
}
