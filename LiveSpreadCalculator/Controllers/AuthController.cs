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

    public AuthController(KiteAuthService authService, KiteSession session, IConfiguration configuration, ILogger<AuthController> logger)
    {
        _authService = authService;
        _session = session;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Returns the Kite login URL the frontend should redirect the browser to.
    /// Register this app's redirect URL (pointing at /api/auth/callback) in the Kite Connect
    /// developer console for your api_key.</summary>
    [HttpGet("login-url")]
    public ActionResult<object> GetLoginUrl() => Ok(new { url = _authService.BuildLoginUrl() });

    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new { isAuthenticated = !string.IsNullOrEmpty(_session.AccessToken) });

    /// <summary>Kite redirects the browser here with request_token after a successful login.
    /// Exchanges it for an access_token, then bounces the browser back to the dashboard.</summary>
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
