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
}
