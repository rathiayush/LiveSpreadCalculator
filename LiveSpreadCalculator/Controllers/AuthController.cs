using System.Net.Http.Headers;
using System.Text.Json;
using LiveSpreadCalculator.Auth;
using LiveSpreadCalculator.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KiteIntegration.Auth;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly KiteAuthService _authService;
    private readonly KiteSession _session;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly CsvUserStore _userStore;
    private readonly JwtTokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly KiteOptions _kiteOptions;

    public AuthController(
        KiteAuthService authService,
        KiteSession session,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        CsvUserStore userStore,
        JwtTokenService tokenService,
        IHttpClientFactory httpClientFactory,
        IOptions<KiteOptions> kiteOptions)
    {
        _authService = authService;
        _session = session;
        _configuration = configuration;
        _logger = logger;
        _userStore = userStore;
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _kiteOptions = kiteOptions.Value;
    }

    /// <summary>App login. Validates email/password against App_Data/users.csv and, on success,
    /// returns a JWT the client must send as `Authorization: Bearer &lt;token&gt;` on every other
    /// API call and as ?access_token= on the SignalR hub. This is the only unauthenticated way in -
    /// everything else on the server requires the token this endpoint hands out.</summary>
    [AllowAnonymous]
    [HttpPost("token")]
    public ActionResult<object> Login([FromBody] UserLoginRequest request)
    {
        if (!_userStore.ValidateCredentials(request.Email, request.Password))
        {
            _logger.LogWarning("Failed login attempt for {Email}", request.Email);
            return Unauthorized(new { error = "Invalid email or password." });
        }

        var (token, expiresUtc) = _tokenService.CreateToken(request.Email.Trim());
        return Ok(new { token, expiresUtc, email = request.Email.Trim() });
    }

    /// <summary>Returns the Kite login URL the frontend should redirect the browser to.
    /// Register this app's redirect URL (pointing at /api/auth/callback) in the Kite Connect
    /// developer console for your api_key.</summary>
    [HttpGet("login-url")]
    public ActionResult<object> GetLoginUrl() => Ok(new { url = _authService.BuildLoginUrl() });

    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new { isAuthenticated = !string.IsNullOrEmpty(_session.AccessToken) });

    /// <summary>Hands the CURRENT Kite access token back to an already-authenticated frontend, so
    /// it can cache it locally (see kiteAuth.js) and offer it back via POST /api/auth/kite-restore
    /// after a future process restart - this is what avoids a real Kite login every time the
    /// backend restarts while yesterday's token is still genuinely valid (Kite tokens are good
    /// until ~6 AM IST the next day, regardless of this app's own process lifetime). Deliberately
    /// requires the same JWT bearer as everything else - this is not a new anonymous way to read
    /// the Kite token, just a value only an already-logged-in dashboard can fetch for itself.
    /// Call right after a successful Kite login (kiteLogin=success) - see App.jsx.</summary>
    [HttpGet("kite-token")]
    public ActionResult<object> GetKiteToken()
    {
        if (string.IsNullOrEmpty(_session.AccessToken))
            return NotFound(new { error = "No active Kite session to return a token for." });

        return Ok(new { accessToken = _session.AccessToken });
    }

    /// <summary>Restores a Kite access token the frontend cached from an earlier login (see
    /// kiteAuth.js / GET /api/auth/kite-token) back into this process's in-memory KiteSession -
    /// the fix for "every backend restart forces a fresh Kite login" even though the previous
    /// token is often still hours away from Kite's own ~6 AM IST daily expiry. The backend's own
    /// in-memory token is always authoritative when present: this only FILLS the session when it's
    /// currently empty (fresh process start), it never overwrites a token this process already has.
    ///
    /// The offered token is verified LIVE against Kite (a single cheap GET /user/profile call)
    /// before it's ever trusted - a candidate that's actually dead (expired past Kite's own ~6 AM
    /// IST cutoff, revoked by a fresh login elsewhere, or just garbage) is rejected right here,
    /// synchronously, so the caller finds out immediately instead of the UI showing "authenticated"
    /// off an unverified value and only discovering it's dead whenever some other Kite call happens
    /// to run next. isAuthenticated:false in the response means exactly that: the frontend should
    /// drop its cached copy (see App.jsx's loadAuthStatus) and show the Login to Kite button.</summary>
    [HttpPost("kite-restore")]
    public async Task<ActionResult<object>> RestoreKiteToken([FromBody] KiteTokenRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_session.AccessToken))
            return Ok(new { isAuthenticated = true }); // already has a live session - nothing to restore

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Ok(new { isAuthenticated = false });

        if (!await VerifyKiteTokenAsync(request.AccessToken, cancellationToken))
        {
            _logger.LogInformation("Rejected a frontend-cached Kite token during restore - Kite no longer honors it.");
            return Ok(new { isAuthenticated = false });
        }

        _session.AccessToken = request.AccessToken;
        _logger.LogInformation("Kite session restored from a frontend-cached access token (verified live against Kite).");
        return Ok(new { isAuthenticated = true });
    }

    /// <summary>Cheapest possible "is this access token still honored by Kite" check - GET
    /// /user/profile with the candidate token, same Authorization header shape every other
    /// Kite-calling service in this app uses (see KitePositionsService/KiteMarginService). Only
    /// used by RestoreKiteToken above; every other Kite call site already gets equivalent
    /// verification "for free" as a side effect of the request it was going to make anyway (see
    /// KiteAuthFailureHandler), so this dedicated check exists purely for the restore path, where
    /// there's no other Kite call happening yet to naturally reveal an invalid token.</summary>
    private async Task<bool> VerifyKiteTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_kiteOptions.RestBaseUrl}/user/profile");
            request.Headers.Add("X-Kite-Version", "3");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_kiteOptions.ApiKey}:{accessToken}");

            using var response = await client.SendAsync(request, cancellationToken);
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

    /// <summary>Kite redirects the browser here with request_token after a successful login.
    /// Exchanges it for an access_token, then bounces the browser back to the dashboard. Must be
    /// anonymous: it's an external redirect from Kite's servers, which can't attach our Bearer
    /// token. The request_token itself is the short-lived secret Kite vouches for here.</summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery(Name = "request_token")] string? requestToken, [FromQuery] string? status, CancellationToken cancellationToken)
    {
        var dashboardUrl = _configuration["Dashboard:BaseUrl"] ?? "https://localhost:63061";

        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(requestToken))
            return Redirect($"{dashboardUrl}?kiteLogin=failed");

        try
        {
            await _authService.ExchangeRequestTokenAsync(requestToken, cancellationToken);
            return Redirect($"{dashboardUrl}?kiteLogin=success");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kite token exchange failed");
            return Redirect($"{dashboardUrl}?kiteLogin=failed");
        }
    }
}
