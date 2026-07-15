using LiveSpreadCalculator.Auth;
using LiveSpreadCalculator.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public AuthController(
        KiteAuthService authService,
        KiteSession session,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        CsvUserStore userStore,
        JwtTokenService tokenService)
    {
        _authService = authService;
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

    /// <summary>Returns the Kite login URL the frontend should redirect the browser to.
    /// Register this app's redirect URL (pointing at /api/auth/callback) in the Kite Connect
    /// developer console for your api_key.</summary>
    [HttpGet("login-url")]
    public ActionResult<object> GetLoginUrl() => Ok(new { url = _authService.BuildLoginUrl() });

    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new { isAuthenticated = !string.IsNullOrEmpty(_session.AccessToken) });

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
