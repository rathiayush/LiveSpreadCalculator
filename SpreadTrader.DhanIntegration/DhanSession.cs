using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.DhanIntegration;

/// <summary>
/// In-memory holder for the live Dhan access token and the account id it belongs to.
///
/// Unlike Kite - where a token can only ever come from a browser login - Dhan lets you generate a
/// long-lived token in the web portal, so this seeds itself from <see cref="DhanOptions.AccessToken"/>
/// at construction. That means a correctly configured deployment is authenticated the instant it
/// boots, with no login step at all. The consent flow (see DhanAuthService) overwrites both fields
/// when it is used instead.
/// </summary>
public sealed class DhanSession : IBrokerSession
{
    private readonly DhanOptions _options;

    public DhanSession(IOptions<DhanOptions> options)
    {
        _options = options.Value;
        AccessToken = string.IsNullOrWhiteSpace(_options.AccessToken) ? null : _options.AccessToken;
        ClientId = string.IsNullOrWhiteSpace(_options.ClientId) ? null : _options.ClientId;
    }

    public string? AccessToken { get; set; }

    /// <summary>Settable (unlike Kite's, which is derived from config) because Dhan's consent flow
    /// returns the dhanClientId alongside the token - a partner app authenticating on behalf of a
    /// user may not have had it configured up front.</summary>
    public string? ClientId { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(ClientId);

    /// <summary>Raised by <see cref="ClearAccessToken"/> - the one signal
    /// <see cref="Auth.DhanTotpAutoRenewalHostedService"/> needs to react to a dead session
    /// immediately instead of waiting for its next scheduled renewal. Not raised by an ordinary
    /// successful login (setting AccessToken directly), only by the "this token just died" path.
    /// </summary>
    public event Action? SessionInvalidated;

    /// <summary>The one place a dead token is recorded - see DhanAuthFailureHandler, the only
    /// caller. Clears the token AND tells anything waiting on a live session (the TOTP
    /// auto-renewal loop) to try again right away, rather than on its own timer.</summary>
    public void ClearAccessToken()
    {
        AccessToken = null;
        SessionInvalidated?.Invoke();
    }
}
