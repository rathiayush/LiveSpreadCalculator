using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.KotakIntegration.Auth;

/// <summary>
/// Kotak Neo's login handshake - a two-step TOTP+MPIN flow with NO browser/OAuth step at all,
/// unlike both Kite (browser redirect, request_token) and Dhan's Consent mode (browser redirect,
/// tokenId). <see cref="SupportsInteractiveLogin"/> is therefore always false and
/// <see cref="BuildLoginUrlAsync"/>/<see cref="CompleteLoginAsync"/> always fail - the ONLY real
/// entry point is <see cref="GenerateAccessTokenViaTotpLoginAsync"/>, driven by
/// KotakSessionAutoRenewalHostedService exactly the way DhanTotpAutoRenewalHostedService drives
/// DhanAuthService.GenerateAccessTokenViaTotpAsync (same shape, one more step):
///
///  1. totp_login: POST {LoginBaseUrl}/login/1.0/tradeApiLogin, JSON body
///     {"mobileNumber","ucc","totp"} (a fresh 6-digit RFC 6238 code from KotakOptions.TotpSecret -
///     see TotpGenerator), headers Authorization={ConsumerKey}, neo-fin-key={NeoFinKey}. Returns a
///     short-lived "view token" (<c>data.token</c>) + session id (<c>data.sid</c>) that authenticate
///     ONLY the next call, not the trading API.
///  2. totp_validate: POST {LoginBaseUrl}/login/1.0/tradeApiValidate, JSON body {"mpin"}, headers
///     Authorization={ConsumerKey}, sid={view sid from step 1}, Auth={view token from step 1},
///     neo-fin-key={NeoFinKey}. Returns the actual session - <c>data.token</c> ("edit token") and
///     <c>data.sid</c> ("edit sid") - which is what KotakApiClient.SendSessionAsync sends on every
///     subsequent trading call (orders, positions, margin, limits) as the Auth/Sid headers.
///
/// BOTH LOGIN CALLS GO TO KotakOptions.LoginBaseUrl, NOT RestBaseUrl - confirmed by Kotak's own
/// Trade API Authentication docs, which hardcode a fixed host (mis.kotaksecurities.com) for login
/// regardless of environment. RestBaseUrl only comes into play afterwards, once step 2's response
/// hands back the real trading host (see KotakSession.BaseUrl).
///
/// Everything else here (exact header names, JSON field names, endpoint paths, response shapes) is
/// now CONFIRMED against Kotak's own Trade API docs, not just the reverse-engineered Python SDK
/// source this was originally built from.
/// </summary>
public sealed class KotakAuthService : IBrokerAuthService
{
    /// <summary>No documented fixed session lifetime was found for Kotak Neo (unlike Kite's daily
    /// 6 AM IST cutoff or Dhan's fixed 24h) - this is a conservative placeholder so the dashboard's
    /// client-side token cache still expires SOMETIME rather than caching forever, pending real
    /// confirmation of Kotak's actual session validity window.</summary>
    private static readonly TimeSpan AssumedSessionValidity = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly KotakOptions _options;
    private readonly KotakSession _session;
    private readonly ILogger<KotakAuthService> _logger;

    public KotakAuthService(HttpClient httpClient, IOptions<KotakOptions> options, KotakSession session, ILogger<KotakAuthService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public string PlatformName => "Kotak Neo";

    public bool SupportsInteractiveLogin => false;

    // Kotak's "access token" (the edit token from totp_validate) is minted only by completing the
    // TOTP+MPIN handshake server-side - there is no Kotak portal that hands a user a copy-pasteable
    // token the way Dhan's does, so a manual-entry box here would only ever accept something that
    // can't possibly work. False, unlike every other platform - see IBrokerAuthService's doc comment.
    public bool SupportsManualToken => false;

    public string NotAuthenticatedGuidance =>
        "Kotak Neo doesn't use a browser login here - it authenticates automatically via TOTP+MPIN, " +
        "the same way Dhan's TOTP mode does. The server should mint its own session within a few " +
        "seconds of starting up (or of Kotak becoming the active broker platform), with nothing for " +
        "you to do. If this message is still here after that, check the server logs for \"Kotak TOTP " +
        "login failed\" - the usual causes are a wrong Kotak:Mpin, a Kotak:TotpSecret copied " +
        "incorrectly (use the manual-entry text, not the QR code), or Kotak:MobileNumber/Kotak:Ucc " +
        "left blank.";

    public Task<string> BuildLoginUrlAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Kotak Neo has no browser login flow - it authenticates automatically via TOTP+MPIN. " +
            "Set Kotak:MobileNumber, Kotak:Ucc, Kotak:Mpin and Kotak:TotpSecret instead.");

    public Task<BrokerLoginResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string?> callbackQuery, CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerLoginResult(false, null, null, null,
            "Kotak Neo has no login callback to complete - it authenticates automatically via TOTP+MPIN."));

    /// <summary>Cheapest "is this session still good" check available without a dedicated Kotak
    /// profile/whoami endpoint confirmed - re-runs step 2 (totp_validate) with the given token
    /// standing in for a freshly-obtained one is not meaningful here (validate consumes a view
    /// token, not the final session token), so this instead does the one thing guaranteed to prove
    /// liveness: a real, cheap authenticated call. Uses the limits endpoint, which every session
    /// mode needs anyway. Returns false on any failure, including network errors, matching every
    /// other broker's VerifyAccessTokenAsync contract.</summary>
    public async Task<bool> VerifyAccessTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(_session.Sid))
            return false;

        try
        {
            var baseUrl = _session.BaseUrl ?? _options.RestBaseUrl;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/quick/user/limits");
            request.Headers.TryAddWithoutValidation("Sid", _session.Sid);
            request.Headers.TryAddWithoutValidation("Auth", accessToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kotak session verification failed (network error or unexpected response) - treating the candidate session as invalid.");
            return false;
        }
    }

    public DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc) => issuedAtUtc.Add(AssumedSessionValidity);

    /// <summary>
    /// Runs the full two-step TOTP+MPIN handshake and, on success, writes the resulting session
    /// (edit token + edit sid) straight to KotakSession - the same "every other call site picks it
    /// up on its very next request, no further plumbing" contract DhanAuthService.
    /// GenerateAccessTokenViaTotpAsync documents. Only KotakSessionAutoRenewalHostedService calls
    /// this - see its doc comment for the renewal schedule (startup, proactive, and reactive on
    /// KotakSession.SessionInvalidated).
    /// </summary>
    public async Task<BrokerLoginResult> GenerateAccessTokenViaTotpLoginAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasLoginCredentials)
            return new BrokerLoginResult(false, null, null, null,
                "Kotak:ConsumerKey / Kotak:MobileNumber / Kotak:Ucc / Kotak:Mpin / Kotak:TotpSecret are not fully configured for TOTP auto-login.");

        var step1 = await LoginStep1Async(cancellationToken);
        if (!step1.Success)
            return new BrokerLoginResult(false, null, null, null, step1.Error);

        var step2 = await LoginStep2Async(step1.ViewSid!, step1.ViewToken!, cancellationToken);
        if (!step2.Success)
            return new BrokerLoginResult(false, null, null, null, step2.Error);

        _session.AccessToken = step2.EditToken;
        _session.Sid = step2.EditSid;
        if (!string.IsNullOrWhiteSpace(step2.BaseUrl))
            _session.BaseUrl = step2.BaseUrl;
        if (!string.IsNullOrWhiteSpace(step2.DataCenter))
            _session.DataCenter = step2.DataCenter;

        var expiresUtc = GetTokenExpiryUtc(DateTimeOffset.UtcNow);
        return new BrokerLoginResult(true, step2.EditToken, _session.ClientId, expiresUtc, null);
    }

    private readonly record struct Step1Result(bool Success, string? ViewSid, string? ViewToken, string? Error);

    private async Task<Step1Result> LoginStep1Async(CancellationToken cancellationToken)
    {
        string totp;
        try
        {
            totp = TotpGenerator.GenerateCode(_options.TotpSecret!);
        }
        catch (FormatException ex)
        {
            return new Step1Result(false, null, null, $"Kotak:TotpSecret is not a valid base32 TOTP secret: {ex.Message}");
        }

        var payload = JsonSerializer.Serialize(new
        {
            mobileNumber = _options.MobileNumber,
            ucc = _options.Ucc,
            totp,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.LoginBaseUrl.TrimEnd('/')}/login/1.0/tradeApiLogin");
        request.Headers.TryAddWithoutValidation("Authorization", _options.ConsumerKey);
        request.Headers.TryAddWithoutValidation("neo-fin-key", _options.NeoFinKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kotak TOTP login step 1 failed (HTTP {Status}): {Body}", (int)response.StatusCode, Truncate(body));
                return new Step1Result(false, null, null, $"Kotak rejected the TOTP login (HTTP {(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.TryGetProperty("data", out var d) ? d : document.RootElement;

            var viewToken = data.TryGetProperty("token", out var t) ? t.GetString() : null;
            var viewSid = data.TryGetProperty("sid", out var s) ? s.GetString() : null;

            if (string.IsNullOrWhiteSpace(viewToken) || string.IsNullOrWhiteSpace(viewSid))
                return new Step1Result(false, null, null, "Kotak's TOTP login response contained no token/sid.");

            return new Step1Result(true, viewSid, viewToken, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kotak TOTP login step 1 failed (network error or unexpected response).");
            return new Step1Result(false, null, null, ex.Message);
        }
    }

    private readonly record struct Step2Result(bool Success, string? EditToken, string? EditSid, string? BaseUrl, string? DataCenter, string? Error);

    private async Task<Step2Result> LoginStep2Async(string viewSid, string viewToken, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { mpin = _options.Mpin });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.LoginBaseUrl.TrimEnd('/')}/login/1.0/tradeApiValidate");
        request.Headers.TryAddWithoutValidation("Authorization", _options.ConsumerKey);
        request.Headers.TryAddWithoutValidation("sid", viewSid);
        request.Headers.TryAddWithoutValidation("Auth", viewToken);
        request.Headers.TryAddWithoutValidation("neo-fin-key", _options.NeoFinKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kotak TOTP login step 2 (MPIN validate) failed (HTTP {Status}): {Body}", (int)response.StatusCode, Truncate(body));
                return new Step2Result(false, null, null, null, null, $"Kotak rejected the MPIN validation (HTTP {(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.TryGetProperty("data", out var d) ? d : document.RootElement;

            var editToken = data.TryGetProperty("token", out var t) ? t.GetString() : null;
            var editSid = data.TryGetProperty("sid", out var s) ? s.GetString() : null;
            // Confirmed by Kotak's own trade-api-guide ("Save your TRADING_TOKEN, TRADING_SID, and
            // BASE_URL for all subsequent API calls") - see KotakSession.BaseUrl's doc comment.
            var baseUrl = data.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
            // Confirmed by the Order & Position Streaming docs - see KotakSession.DataCenter's doc comment.
            var dataCenter = data.TryGetProperty("dataCenter", out var dc) ? dc.GetString() : null;

            if (string.IsNullOrWhiteSpace(editToken) || string.IsNullOrWhiteSpace(editSid))
                return new Step2Result(false, null, null, null, null, "Kotak's MPIN validation response contained no token/sid.");

            return new Step2Result(true, editToken, editSid, baseUrl, dataCenter, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kotak TOTP login step 2 (MPIN validate) failed (network error or unexpected response).");
            return new Step2Result(false, null, null, null, null, ex.Message);
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";
}
