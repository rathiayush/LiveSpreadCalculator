using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.DhanIntegration.Auth;

/// <summary>
/// Dhan's login handshake. IBrokerAuthService's browser-facing surface (BuildLoginUrlAsync /
/// CompleteLoginAsync) only ever serves the CONSENT flow described below - TOTP auto-login is a
/// separate, non-interactive path with its own entry point
/// (<see cref="GenerateAccessTokenViaTotpAsync"/>) driven by DhanTotpAutoRenewalHostedService, not
/// by anything the dashboard calls.
///
/// STATIC TOKEN (DhanOptions.AuthMode "Static", the default, no AppId/AppSecret) - there is no
/// browser flow and no code minting tokens on your behalf. A token generated in the Dhan web
/// portal is supplied through configuration and seeded into DhanSession at startup.
/// <see cref="SupportsInteractiveLogin"/> is false (unless consent credentials are ALSO set - see
/// below) and the dashboard hides its login button rather than offering one that cannot work.
///
/// TOTP (DhanOptions.AuthMode "Totp") - also no browser, but also no manual token paste, ever.
/// GenerateAccessTokenViaTotpAsync computes the current 6-digit code from DhanOptions.TotpSecret
/// (RFC 6238 - see TotpGenerator) and calls Dhan's own generateAccessToken endpoint with it plus
/// DhanOptions.Pin. DhanTotpAutoRenewalHostedService is what actually calls this, on its own
/// schedule (startup, ~1h before every 24h expiry, and immediately on DhanSession.
/// SessionInvalidated) - nothing in this class or the dashboard drives it directly.
///
/// CONSENT FLOW (AppId + AppSecret set) - Dhan's three-legged OAuth-style handshake, independent
/// of AuthMode and available as a manual fallback on top of either mode above:
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
    /// <summary>Dhan's fixed 24-hour validity, shared by both flows this app drives
    /// programmatically (Consent and TOTP) - as opposed to the Static flow's
    /// AccessTokenValidityDays, which mirrors whatever validity was chosen in the web portal.
    /// </summary>
    private static readonly TimeSpan ProgrammaticTokenValidity = TimeSpan.FromHours(24);

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

    /// <summary>Only shown when SupportsInteractiveLogin is false, i.e. no consent credentials are
    /// configured - so this only has to distinguish Static from Totp, not account for consent
    /// being layered on top (that case gets the login button instead, see SupportsInteractiveLogin).
    /// </summary>
    public string NotAuthenticatedGuidance => _options.UseTotpAuth
        ? "TOTP auto-login is on - the server should mint its own access token within a few seconds "
          + "of starting up, with nothing for you to do. If this message is still here after that, "
          + "something is actually failing: check the server logs for \"Dhan TOTP login failed\" or "
          + "\"Dhan TOTP auto-login failed\" - the usual causes are a wrong Dhan:Pin, a TotpSecret "
          + "copied incorrectly (use the manual-entry text from Dhan's Setup TOTP page, not the QR "
          + "code), or Dhan:ClientId left blank. As a temporary override you can still paste a token "
          + "generated in the Dhan web portal below; it won't be overwritten until the auto-login "
          + "loop's next scheduled renewal or the next time this token is rejected."
        : "Dhan doesn't use a browser login here - generate an access token in the Dhan web portal "
          + "(Profile → DhanHQ Trading APIs) and paste it below. It's verified against Dhan "
          + "before being accepted, and takes effect immediately without a restart.";

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

    /// <summary>
    /// Mints a fresh access token with no browser and no human involved: computes the current
    /// TOTP code from <see cref="DhanOptions.TotpSecret"/> (see TotpGenerator) and calls
    /// POST {auth}/app/generateAccessToken with dhanClientId + pin + totp as query parameters,
    /// exactly as documented at https://dhanhq.co/docs/v2/authentication/. Only
    /// DhanTotpAutoRenewalHostedService calls this - see the class doc comment for its schedule.
    ///
    /// On success, writes the token straight to DhanSession, the same as CompleteLoginAsync does
    /// for the consent flow, so every other Dhan call site picks it up on its very next request
    /// with no further plumbing - DhanApiClient/DhanMarketFeedClient both read
    /// DhanSession.AccessToken fresh on each attempt rather than caching a copy.
    /// </summary>
    public async Task<BrokerLoginResult> GenerateAccessTokenViaTotpAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasTotpCredentials)
            return new BrokerLoginResult(false, null, null, null,
                "Dhan:ClientId / Dhan:Pin / Dhan:TotpSecret are not fully configured for TOTP auto-login.");

        string totp;
        try
        {
            totp = TotpGenerator.GenerateCode(_options.TotpSecret!);
        }
        catch (FormatException ex)
        {
            return new BrokerLoginResult(false, null, null, null,
                $"Dhan:TotpSecret is not a valid base32 TOTP secret: {ex.Message}");
        }

        var url = $"{_options.AuthBaseUrl}/app/generateAccessToken" +
                  $"?dhanClientId={Uri.EscapeDataString(_options.ClientId)}" +
                  $"&pin={Uri.EscapeDataString(_options.Pin!)}" +
                  $"&totp={Uri.EscapeDataString(totp)}";

        try
        {
            using var response = await _httpClient.PostAsync(url, content: null, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // A wrong PIN/secret, a clock far enough out of sync to invalidate the TOTP
                // window, or Dhan-side throttling all land here - never logged at Error, since
                // the caller retries with backoff and a single failed attempt is routine, not an
                // incident.
                _logger.LogWarning("Dhan TOTP login failed (HTTP {Status}): {Body}", (int)response.StatusCode, Truncate(body));
                return new BrokerLoginResult(false, null, null, null,
                    $"Dhan rejected the TOTP login (HTTP {(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = root.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return new BrokerLoginResult(false, null, null, null, "Dhan's TOTP login response contained no accessToken.");

            var clientId = root.TryGetProperty("dhanClientId", out var cid) ? cid.GetString() : null;
            if (string.IsNullOrWhiteSpace(clientId)) clientId = _options.ClientId;

            _session.AccessToken = accessToken;
            _session.ClientId = clientId;

            var expiresUtc = ReadExpiry(root) ?? GetTokenExpiryUtc(DateTimeOffset.UtcNow);
            return new BrokerLoginResult(true, accessToken, clientId, expiresUtc, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Dhan TOTP login failed (network error or unexpected response).");
            return new BrokerLoginResult(false, null, null, null, ex.Message);
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";

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

    /// <summary>Consent-flow and TOTP tokens both last a fixed 24 hours from issue; a token
    /// generated in the web portal (static mode) lasts for whatever validity Dhan currently
    /// grants there, mirrored in Dhan:AccessTokenValidityDays (24 hours as of this writing - see
    /// its doc comment). Unlike Kite there is no fixed daily wall-clock cutoff, so this is always
    /// a duration from issue rather than "the next 6 AM IST". Checked in this order because a
    /// deployment can have consent credentials configured as a manual fallback on top of Totp
    /// mode - in that combination the token actually in play was issued by whichever flow last
    /// ran, but both share the same 24h validity, so the two checks agree either way.</summary>
    public DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc) =>
        _options.UseTotpAuth || _options.HasConsentCredentials
            ? issuedAtUtc.Add(ProgrammaticTokenValidity)
            : issuedAtUtc.AddDays(Math.Max(1, _options.AccessTokenValidityDays));
}
