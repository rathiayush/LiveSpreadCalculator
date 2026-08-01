using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.KiteIntegration;

namespace SpreadTrader.KiteIntegration.Auth;

/// <summary>
/// Implements Kite Connect's login handshake: build the login URL, then exchange the
/// request_token the user gets redirected back with for an access_token.
/// https://kite.trade/docs/connect/v3/user/#login-flow
/// Note: Zerodha does not officially endorse automating this daily login; this only implements
/// the token-exchange half, not automated credential entry.
/// </summary>
public sealed class KiteAuthService : IBrokerAuthService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly ILogger<KiteAuthService> _logger;

    public KiteAuthService(
        HttpClient httpClient,
        IOptions<KiteOptions> options,
        KiteSession session,
        ILogger<KiteAuthService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public string PlatformName => "Zerodha Kite";

    /// <summary>Kite's browser login is the only way to obtain a token, so this is always true - a
    /// missing api_key is a misconfiguration to surface at login time, not a reason to tell the
    /// dashboard there is no login flow at all.</summary>
    public bool SupportsInteractiveLogin => true;

    // Irrelevant on Kite - SupportsInteractiveLogin is always true, so the dashboard always shows
    // the Login button and never consults this at all. True for interface-completeness only.
    public bool SupportsManualToken => true;

    /// <summary>Never actually shown - SupportsInteractiveLogin is always true on Kite, so the
    /// dashboard always renders the login button instead of this text. Implemented anyway so every
    /// IBrokerAuthService gives a real answer rather than an empty string.</summary>
    public string NotAuthenticatedGuidance =>
        "Click Login to Zerodha Kite and complete the login/2FA flow. Kite tokens expire daily around 6 AM IST, so this is needed once each trading day.";

    public Task<string> BuildLoginUrlAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Kite:ApiKey is not configured - cannot build a login URL.");

        return Task.FromResult(BuildLoginUrl());
    }

    public string BuildLoginUrl() =>
        $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(_options.ApiKey)}";

    /// <summary>Kite redirects back with <c>request_token</c> plus a <c>status</c> of "success"
    /// once the user actually completed the login (rather than cancelling out of it).</summary>
    public async Task<BrokerLoginResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string?> callbackQuery,
        CancellationToken cancellationToken)
    {
        callbackQuery.TryGetValue("status", out var status);
        callbackQuery.TryGetValue("request_token", out var requestToken);

        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(requestToken))
            return new BrokerLoginResult(false, null, null, null, "Kite did not redirect back with a successful request_token.");

        try
        {
            var accessToken = await ExchangeRequestTokenAsync(requestToken, cancellationToken);
            return new BrokerLoginResult(
                true, accessToken, _session.ClientId, GetTokenExpiryUtc(DateTimeOffset.UtcNow), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kite token exchange failed");
            return new BrokerLoginResult(false, null, null, null, ex.Message);
        }
    }

    public async Task<string> ExchangeRequestTokenAsync(string requestToken, CancellationToken cancellationToken)
    {
        var checksum = ComputeChecksum(_options.ApiKey, requestToken, _options.ApiSecret);
        var form = new Dictionary<string, string>
        {
            ["api_key"] = _options.ApiKey,
            ["request_token"] = requestToken,
            ["checksum"] = checksum,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.RestBaseUrl}/session/token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Add("X-Kite-Version", "3");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : body;
            throw new InvalidOperationException($"Kite token exchange failed: {message}");
        }

        var accessToken = root.GetProperty("data").GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Kite response did not contain an access_token.");

        _session.AccessToken = accessToken;
        return accessToken;
    }

    /// <summary>Cheapest possible "is this access token still honored by Kite" check - GET
    /// /user/profile with the candidate token, the same Authorization header shape every other
    /// Kite-calling service in this app uses. Only needed on the restore path; every other Kite
    /// call site gets equivalent verification for free as a side effect of the request it was
    /// going to make anyway (see KiteAuthFailureHandler).</summary>
    public async Task<bool> VerifyAccessTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl}/user/profile");
            request.Headers.Add("X-Kite-Version", "3");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return string.Equals(doc.RootElement.GetProperty("status").GetString(), "success", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kite token verification failed (network error or unexpected response) - treating the candidate token as invalid.");
            return false;
        }
    }

    /// <summary>Kite invalidates every access token at roughly 6:00 AM IST daily, regardless of
    /// when it was issued. IST is UTC+5:30 with no DST, so 6:00 IST is 00:30 UTC on the same
    /// calendar date - this returns the next such instant at or after
    /// <paramref name="issuedAtUtc"/>. Mirrors what the dashboard's token cache used to compute
    /// client-side, now derived server-side so the cache doesn't have to know one broker's
    /// schedule.</summary>
    public DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc)
    {
        // 00:30 UTC on the same calendar date as issuedAtUtc.
        var todayCutoff = new DateTimeOffset(issuedAtUtc.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(30);
        return issuedAtUtc < todayCutoff ? todayCutoff : todayCutoff.AddDays(1);
    }

    private static string ComputeChecksum(string apiKey, string requestToken, string apiSecret)
    {
        var raw = apiKey + requestToken + apiSecret;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
