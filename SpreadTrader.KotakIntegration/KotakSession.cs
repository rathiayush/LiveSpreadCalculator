using SpreadTrader.Core.Brokers;

namespace SpreadTrader.KotakIntegration;

/// <summary>
/// In-memory holder for the live Kotak Neo session. Unlike Dhan there is no portal-generated
/// long-lived token to seed from at construction - Kotak's session only ever comes from completing
/// the two-step TOTP+MPIN login (see KotakAuthService), so this starts empty and
/// KotakSessionAutoRenewalHostedService logs in within seconds of boot, the same promise Dhan's
/// TOTP mode makes.
///
/// Kotak's login response carries THREE values every subsequent call needs, not just one bearer
/// token - see KotakAuthService.CompleteTotpLoginAsync:
///  - <see cref="AccessToken"/> is Kotak's "edit token" (<c>Auth</c> header on every REST call).
///  - <see cref="Sid"/> is the paired session id (<c>Sid</c> header, sent alongside Auth on every
///    call - Kotak's REST API is meaningless with only one of the two).
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
