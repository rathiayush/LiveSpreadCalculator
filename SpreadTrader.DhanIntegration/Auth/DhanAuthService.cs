using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.DhanIntegration.Auth;

/// <summary>
/// Dhan's login handshake. Two modes, chosen by whether partner credentials are configured (see
/// DhanOptions):
///
/// STATIC TOKEN (no AppId/AppSecret) - there is no browser flow at all. A token generated in the
/// Dhan web portal is supplied through configuration and seeded into DhanSession at startup, so
/// the app is authenticated from boot. <see cref="SupportsInteractiveLogin"/> is false and the
/// dashboard hides its login button rather than offering one that cannot work.
///
/// CONSENT FLOW (AppId + AppSecret set) - Dhan's three-legged OAuth-style handshake:
///   1. POST {auth}/app/generate-consent?client_id={dhanClientId}, authenticated with the
///      app_id/app_secret headers, returns a <c>consentAppId</c>.
///   2. The browser is sent to {auth}/login/consentApp-login?consentAppId={id}, where the user
///      authenticates with Dhan directly (this app never sees their credentials).
///   3. Dhan redirects back to the app's registered redirect URL with a <c>tokenId</c>, which is
///      exchanged at GET {auth}/app/consumeApp-consent?tokenId={id} for the access token and the
///      dhanClientId it belongs to.
///
/// Note this differs structurally from Kite's flow in a way the abstraction has to accommodate:
/// Kite's login URL is a pure string built from the api_key, whereas step 1 here is a real
/// server-to-server call - which is why IBrokerAuthService.BuildLoginUrlAsync is async.
/// </summary>
public sealed class DhanAuthService : IBrokerAuthService
{
    /// <summary>Dhan's consent-flow tokens are valid for 24 hours from issue.</summary>
    private static readonly TimeSpan ConsentTokenValidity = TimeSpan.FromHours(24);

    private readonly HttpClient _httpClient;
    private readonly DhanOptions _options;
    private readonly DhanSession _session;
    private readonly ILogger<DhanAuthService> _logger;

    public DhanAuthService(
        HttpClient httpClient,
        IOptions<DhanOptions> options,
        DhanSession session,
        ILogger<DhanAuthService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public string PlatformName => "Dhan";

    public bool SupportsInteractiveLogin => _options.HasConsentCredentials;

    public async Task<string> BuildLoginUrlAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasConsentCredentials)
            throw new InvalidOperationException(
                "Dhan is configured for static-token access (no Dhan:AppId / Dhan:AppSecret), which has no browser login flow. " +
                "Generate an access token in the Dhan web portal and set Dhan:AccessToken instead.");

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Dhan:ClientId is not configured - it is required to generate a login consent.");

        var consentAppId = await GenerateConsentAsync(cancellationToken);
        return $"{_options.AuthBaseUrl}/login/consentApp-login?consentAppId={Uri.EscapeDataString(consentAppId)}";
    }

    /// <summary>Step 1 of the consent flow. The app credentials go in headers, not the body, and
    /// the Dhan account the consent is for goes in the query string.</summary>
    private async Task<string> GenerateConsentAsync(CancellationToken cancellationToken)
    {
        var url = $"{_options.AuthBaseUrl}/app/generate-consent?client_id={Uri.EscapeDataString(_options.ClientId)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("app_id", _options.AppId);
        request.Headers.TryAddWithoutValidation("app_secret", _options.AppSecret);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Dhan consent generation failed (HTTP {(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var consentAppId = document.RootElement.TryGetProperty("consentAppId", out var id) ? id.GetString() : null;

        return string.IsNullOrWhiteSpace(consentAppId)
            ? throw new InvalidOperationException($"Dhan consent generation returned no consentAppId: {body}")
            : consentAppId;
    }

    /// <summary>Step 3 of the consent flow. Dhan redirects back with <c>tokenId</c> (as opposed to
    /// Kite's <c>request_token</c>), which is why AuthController hands the whole callback query
    /// through untouched instead of naming a parameter.</summary>
    public async Task<BrokerLoginResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string?> callbackQuery,
        CancellationToken cancellationToken)
    {
        if (!_options.HasConsentCredentials)
            return new BrokerLoginResult(false, null, null, null,
                "Dhan is configured for static-token access - there is no login callback to complete.");

        callbackQuery.TryGetValue("tokenId", out var tokenId);
        if (string.IsNullOrWhiteSpace(tokenId))
            return new BrokerLoginResult(false, null, null, null, "Dhan did not redirect back with a tokenId.");

        try
        {
            var url = $"{_options.AuthBaseUrl}/app/consumeApp-consent?tokenId={Uri.EscapeDataString(tokenId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("app_id", _options.AppId);
            request.Headers.TryAddWithoutValidation("app_secret", _options.AppSecret);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Dhan consent consumption failed (HTTP {Status}): {Body}", (int)response.StatusCode, body);
                return new BrokerLoginResult(false, null, null, null, $"Dhan rejected the login token (HTTP {(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = root.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return new BrokerLoginResult(false, null, null, null, "Dhan's consent response contained no accessToken.");

            // Dhan returns the account the token belongs to. Prefer it over the configured value:
            // for a partner app authenticating on behalf of a user, it may be the only source.
            var clientId = root.TryGetProperty("dhanClientId", out var cid) ? cid.GetString() : null;
            if (string.IsNullOrWhiteSpace(clientId)) clientId = _options.ClientId;

            _session.AccessToken = accessToken;
            _session.ClientId = clientId;

            var expiresUtc = ReadExpiry(root) ?? GetTokenExpiryUtc(DateTimeOffset.UtcNow);
            _logger.LogInformation("Dhan login completed for client {ClientId}; token valid until {ExpiresUtc:u}.", clientId, expiresUtc);

            return new BrokerLoginResult(true, accessToken, clientId, expiresUtc, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dhan consent consumption failed");
            return new BrokerLoginResult(false, null, null, null, ex.Message);
        }
    }

    /// <summary>Dhan reports the token's own expiry on the consent response; use it when present
    /// rather than assuming the documented 24 hours, so a change on Dhan's side doesn't silently
    /// leave the dashboard caching a token past its real life.</summary>
    private static DateTimeOffset? ReadExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expiryTime", out var expiry)) return null;
        var raw = expiry.ValueKind == JsonValueKind.String ? expiry.GetString() : null;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>GET /v2/profile is Dhan's own "is this token still good" endpoint - it returns the
    /// account's details and token validity, and 401s with DH-901 when the token is dead.</summary>
    public async Task<bool> VerifyAccessTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        var clientId = _session.ClientId ?? _options.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning("Cannot verify a Dhan access token without a client id - set Dhan:ClientId.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl}/profile");
            request.Headers.TryAddWithoutValidation("access-token", accessToken);
            request.Headers.TryAddWithoutValidation("client-id", clientId);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dhan token verification failed (network error or unexpected response) - treating the candidate token as invalid.");
            return false;
        }
    }

    /// <summary>Consent-flow tokens last 24 hours; a token generated in the web portal lasts for
    /// whatever validity was chosen there (up to 30 days), which the operator mirrors in
    /// Dhan:AccessTokenValidityDays. Unlike Kite there is no fixed daily wall-clock cutoff, so this
    /// is a duration from issue rather than "the next 6 AM IST".</summary>
    public DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc) =>
        _options.HasConsentCredentials
            ? issuedAtUtc.Add(ConsentTokenValidity)
            : issuedAtUtc.AddDays(Math.Max(1, _options.AccessTokenValidityDays));
}
