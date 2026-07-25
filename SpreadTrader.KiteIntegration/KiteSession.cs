using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.KiteIntegration;

/// <summary>
/// Mutable, in-memory holder for the current Kite access token. Registered as a singleton so
/// the token obtained via the login flow (AuthController) is immediately visible to the ticker
/// and order gateway without a restart. Lost on process restart by design (no persistence).
/// </summary>
public sealed class KiteSession : IBrokerSession
{
    private readonly KiteOptions _options;

    public KiteSession(IOptions<KiteOptions> options)
    {
        _options = options.Value;
    }

    public string? AccessToken { get; set; }

    /// <summary>Kite has no separate account id on the wire - the api_key identifies the app and is
    /// what gets sent alongside the token in the <c>Authorization: token api_key:access_token</c>
    /// header. Surfaced here so IBrokerSession has one meaning across both platforms (on Dhan the
    /// equivalent is the dhanClientId).</summary>
    public string? ClientId => string.IsNullOrEmpty(_options.ApiKey) ? null : _options.ApiKey;

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);
}
