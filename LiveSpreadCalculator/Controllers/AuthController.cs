using LiveSpreadCalculator.Auth;
using LiveSpreadCalculator.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IBrokerAuthService _brokerAuth;
    private readonly IBrokerSession _session;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly CsvUserStore _userStore;
    private readonly JwtTokenService _tokenService;

    public AuthController(
        IBrokerAuthService brokerAuth,
        IBrokerSession session,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        CsvUserStore userStore,
        JwtTokenService tokenService)
    {
        _brokerAuth = brokerAuth;
        _session = session;
        _configuration = configuration;
        _logger = logger;
        _userStore = userStore;
        _tokenService = tokenService;
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

        var email = request.Email.Trim();
        // Bake the user's current security stamp into the token so a later password change can
        // invalidate it (see Program.cs OnTokenValidated).
        var stamp = _userStore.GetSecurityStamp(email);
        var (token, expiresUtc) = _tokenService.CreateToken(email, stamp);
        return Ok(new { token, expiresUtc, email });
    }

    /// <summary>Lets the signed-in user change their own password. Re-verifies the current password,
    /// then stores the new one ENCRYPTED in App_Data/users.csv. Because that rewrites the user's
    /// security stamp, every token issued against the old password - on any device, including this
    /// one - stops validating immediately, so the client is expected to drop its session and send
    /// the user back to the login screen after a success here.</summary>
    [HttpPost("change-password")]
    public ActionResult<object> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        // Identity.Name is the email - set explicitly as a ClaimTypes.Name claim by JwtTokenService.
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            return Unauthorized(new { error = "Not signed in." });

        var result = _userStore.ChangePassword(email, request.CurrentPassword, request.NewPassword);
        if (!result.Ok)
        {
            _logger.LogWarning("Password change rejected for {Email}: {Reason}", email, result.Error);
            return BadRequest(new { error = result.Error });
        }

        _logger.LogInformation("Password changed for {Email}; all existing sessions invalidated.", email);
        return Ok(new { message = "Password updated. Please sign in again." });
    }

    /// <summary>Which broker this deployment is wired to (see Broker:Platform) and whether it has a
    /// browser login flow at all. The dashboard reads this to label its status pill and login
    /// button for the live platform rather than hardcoding one, and to hide the button entirely
    /// when the platform is configured for static-token access (Dhan with a token generated in the
    /// web portal), where a login button could only ever fail.</summary>
    [HttpGet("broker")]
    public ActionResult<object> GetBroker() => Ok(new
    {
        platform = _brokerAuth.PlatformName,
        supportsInteractiveLogin = _brokerAuth.SupportsInteractiveLogin,
        isAuthenticated = _session.IsAuthenticated,
        notAuthenticatedGuidance = _brokerAuth.NotAuthenticatedGuidance,
    });

    /// <summary>Returns the broker login URL the frontend should redirect the browser to. Register
    /// this app's redirect URL (pointing at /api/auth/callback) in the broker's developer console -
    /// the Kite Connect console for a Kite api_key, or the Dhan partner app settings.</summary>
    [HttpGet("login-url")]
    public async Task<ActionResult<object>> GetLoginUrl(CancellationToken cancellationToken)
    {
        try
        {
            var url = await _brokerAuth.BuildLoginUrlAsync(cancellationToken);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            // Misconfiguration (no api key / no partner credentials) or a platform with no login
            // flow at all - a 400 with the reason is far more useful to the dashboard than an
            // empty URL it has to guess about.
            _logger.LogWarning(ex, "Could not build a broker login URL");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new { isAuthenticated = _session.IsAuthenticated });

    /// <summary>Hands the CURRENT broker access token back to an already-authenticated frontend, so
    /// it can cache it locally (see brokerAuth.js) and offer it back via POST /api/auth/broker-restore
    /// after a future process restart - this is what avoids a real broker login every time the
    /// backend restarts while the existing token is still genuinely valid. Deliberately requires
    /// the same JWT bearer as everything else - this is not a new anonymous way to read the broker
    /// token, just a value only an already-logged-in dashboard can fetch for itself.
    ///
    /// The expiry is computed by the active platform (see IBrokerAuthService.GetTokenExpiryUtc)
    /// rather than assumed by the client, because the two brokers expire tokens on completely
    /// different schedules - Kite at a fixed ~6 AM IST daily cutoff, Dhan a fixed duration from
    /// issue.</summary>
    [HttpGet("broker-token")]
    public ActionResult<object> GetBrokerToken()
    {
        if (string.IsNullOrEmpty(_session.AccessToken))
            return NotFound(new { error = "No active broker session to return a token for." });

        return Ok(new
        {
            accessToken = _session.AccessToken,
            expiresUtc = _brokerAuth.GetTokenExpiryUtc(DateTimeOffset.UtcNow),
        });
    }

    /// <summary>Installs an access token supplied by hand, after verifying it live against the
    /// broker. This exists because the two platforms have fundamentally different auth models:
    ///
    ///  - Kite has a MANDATORY browser login. Its tokens die at ~6 AM IST daily and can only be
    ///    reissued by completing that flow, so there is always a login button to fall back on.
    ///  - Dhan does NOT require a browser login. The usual setup is an access token generated in
    ///    the Dhan web portal and supplied through configuration, which means
    ///    SupportsInteractiveLogin is false and there is no login button at all. Without this
    ///    endpoint, the moment such a token expired (or was invalidated mid-session by the auth
    ///    failure handler) the only way to get trading again would be editing configuration and
    ///    restarting a live process holding open positions.
    ///
    /// So: paste a freshly generated token here and the process picks it up immediately. Unlike
    /// /broker-restore, this deliberately REPLACES whatever token the session currently holds -
    /// it's an operator action, not an opportunistic restore. Still requires the app's own JWT,
    /// and the token is verified against the broker before being accepted, so a typo or a dead
    /// token is rejected here rather than silently breaking the next order.</summary>
    [HttpPost("broker-token")]
    public async Task<ActionResult<object>> SetBrokerToken([FromBody] SetBrokerTokenRequest request, CancellationToken cancellationToken)
    {
        var token = request.AccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { error = "Access token is required." });

        if (!await _brokerAuth.VerifyAccessTokenAsync(token, cancellationToken))
        {
            _logger.LogWarning("Rejected a manually supplied broker token - {Platform} does not honor it.", _brokerAuth.PlatformName);
            return BadRequest(new { error = $"{_brokerAuth.PlatformName} rejected that access token. Check it was copied in full and hasn't expired." });
        }

        _session.AccessToken = token;
        _logger.LogInformation("{Platform} access token installed manually via POST /api/auth/broker-token.", _brokerAuth.PlatformName);

        return Ok(new
        {
            isAuthenticated = true,
            expiresUtc = _brokerAuth.GetTokenExpiryUtc(DateTimeOffset.UtcNow),
        });
    }

    /// <summary>Restores a broker access token the frontend cached from an earlier login (see
    /// brokerAuth.js / GET /api/auth/broker-token) back into this process's in-memory session - the
    /// fix for "every backend restart forces a fresh broker login" even though the previous token
    /// is often still hours (or days) away from its own expiry. The backend's own in-memory token
    /// is always authoritative when present: this only FILLS the session when it's currently empty
    /// (fresh process start), it never overwrites a token this process already has.
    ///
    /// The offered token is verified LIVE against the broker (a single cheap profile call) before
    /// it's ever trusted - a candidate that's actually dead (expired, revoked by a fresh login
    /// elsewhere, or just garbage) is rejected right here, synchronously, so the caller finds out
    /// immediately instead of the UI showing "authenticated" off an unverified value and only
    /// discovering it's dead whenever some other broker call happens to run next.
    /// isAuthenticated:false in the response means exactly that: the frontend should drop its
    /// cached copy (see App.jsx's loadAuthStatus) and show the login button.</summary>
    [HttpPost("broker-restore")]
    public async Task<ActionResult<object>> RestoreBrokerToken([FromBody] BrokerTokenRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_session.AccessToken))
            return Ok(new { isAuthenticated = true }); // already has a live session - nothing to restore

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Ok(new { isAuthenticated = false });

        if (!await _brokerAuth.VerifyAccessTokenAsync(request.AccessToken, cancellationToken))
        {
            _logger.LogInformation("Rejected a frontend-cached broker token during restore - {Platform} no longer honors it.", _brokerAuth.PlatformName);
            return Ok(new { isAuthenticated = false });
        }

        _session.AccessToken = request.AccessToken;
        _logger.LogInformation("{Platform} session restored from a frontend-cached access token (verified live against the broker).", _brokerAuth.PlatformName);
        return Ok(new { isAuthenticated = true });
    }

    /// <summary>The broker redirects the browser here after a successful login. Must be anonymous:
    /// it's an external redirect from the broker's servers, which can't attach our Bearer token.
    /// The short-lived token in the query string is the secret the broker vouches for here.
    ///
    /// The entire query string is handed to the active platform's auth service rather than being
    /// destructured here, because the two brokers redirect with different parameters - Kite with
    /// <c>request_token</c> plus a <c>status</c>, Dhan with a <c>tokenId</c>. Keeping that
    /// knowledge inside the platform adapter is what lets this one callback serve both.</summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(CancellationToken cancellationToken)
    {
        var dashboardUrl = _configuration["Dashboard:BaseUrl"] ?? "https://localhost:63061";

        var query = Request.Query.ToDictionary(
            kv => kv.Key,
            kv => (string?)kv.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        var result = await _brokerAuth.CompleteLoginAsync(query, cancellationToken);
        if (!result.Success)
        {
            _logger.LogError("{Platform} login callback failed: {Error}", _brokerAuth.PlatformName, result.Error);
            return Redirect($"{dashboardUrl}?brokerLogin=failed");
        }

        return Redirect($"{dashboardUrl}?brokerLogin=success");
    }
}
