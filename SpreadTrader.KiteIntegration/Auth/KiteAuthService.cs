using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SpreadTrader.KiteIntegration;

namespace SpreadTrader.KiteIntegration.Auth;

/// <summary>
/// Implements Kite Connect's login handshake: build the login URL, then exchange the
/// request_token the user gets redirected back with for an access_token.
/// https://kite.trade/docs/connect/v3/user/#login-flow
/// Note: Zerodha does not officially endorse automating this daily login; this only implements
/// the token-exchange half, not automated credential entry.
/// </summary>
public sealed class KiteAuthService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;

    public KiteAuthService(HttpClient httpClient, IOptions<KiteOptions> options, KiteSession session)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
    }

    public string BuildLoginUrl() =>
        $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(_options.ApiKey)}";

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

    private static string ComputeChecksum(string apiKey, string requestToken, string apiSecret)
    {
        var raw = apiKey + requestToken + apiSecret;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
