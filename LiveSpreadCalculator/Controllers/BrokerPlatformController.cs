using LiveSpreadCalculator.Brokers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Lets the dashboard switch which broker the whole app runs against, without editing config or
/// redeploying. BrokerRegistration only ever wires up one platform's services, once, at process
/// startup (see its doc comment for why there is deliberately no in-process runtime switch) - so
/// "switch to Dhan" can only ever mean "persist that choice, then restart the process so it boots
/// fresh with the other platform wired up instead." This controller does the first half and makes
/// a best-effort attempt at the second.
///
/// The restart itself is NOT guaranteed to be automatic - it depends on something outside this
/// process bringing a stopped process back up (Docker's restart policy, Azure App Service's
/// "Always On" + auto-heal, IIS/ANCM recycling a crashed worker process on the next request). If
/// none of those apply to a given deployment, StopApplication below still shuts the process down,
/// but nothing brings it back - the operator would need to start it manually. Either way, the
/// override is written to disk FIRST, before any attempt to stop the process, so the choice is
/// never lost even if the automatic-restart half doesn't apply here: the next time this process is
/// started, by whatever means, it boots on the newly chosen platform.
/// </summary>
[ApiController]
[Route("api/settings/broker-platform")]
public sealed class BrokerPlatformController : ControllerBase
{
    private readonly BrokerOptions _brokerOptions;
    private readonly string _overridePath;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BrokerPlatformController> _logger;

    public BrokerPlatformController(
        IOptions<BrokerOptions> brokerOptions,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IHostApplicationLifetime lifetime,
        ILogger<BrokerPlatformController> logger)
    {
        _brokerOptions = brokerOptions.Value;
        _overridePath = Path.Combine(environment.ContentRootPath,
            configuration["Storage:BrokerPlatformOverridePath"] ?? "App_Data/broker-platform-override.txt");
        _lifetime = lifetime;
        _logger = logger;
    }

    public sealed record BrokerPlatformResponse(string Platform);

    /// <summary>The platform THIS running process is actually wired up for - not what the override
    /// file says, since those two can briefly disagree (a switch was requested but the restart
    /// hasn't happened, or isn't automatic on this deployment). The dashboard polls this after a
    /// switch to find out when the new process has actually come up.</summary>
    [HttpGet]
    public ActionResult<BrokerPlatformResponse> Get() =>
        Ok(new BrokerPlatformResponse(_brokerOptions.Platform.ToString()));

    public sealed record SwitchRequest(string Platform);

    public sealed record SwitchResponse(string Platform, bool RestartTriggered, string Message);

    /// <summary>Persists the new platform choice, then asks the host to stop - see the class doc
    /// comment for why that second part is best-effort. A no-op (200, RestartTriggered:false) when
    /// the requested platform is already the live one, so a confused double-click never kills a
    /// perfectly good running process for nothing.</summary>
    [HttpPost]
    public ActionResult<SwitchResponse> Switch([FromBody] SwitchRequest request)
    {
        if (!Enum.TryParse<BrokerPlatform>(request.Platform, ignoreCase: true, out var platform))
            return BadRequest(new { error = $"Unknown platform '{request.Platform}'. Use 'Kite' or 'Dhan'." });

        if (platform == _brokerOptions.Platform)
        {
            return Ok(new SwitchResponse(platform.ToString(), RestartTriggered: false,
                $"Already running on {platform} - nothing to switch."));
        }

        BrokerPlatformOverrideStore.Write(_overridePath, platform);
        _logger.LogWarning(
            "Broker platform switch to {Platform} requested via the dashboard (was {Previous}) - " +
            "the override is saved; stopping the process now so it restarts on the new platform.",
            platform, _brokerOptions.Platform);

        // Delayed so this request's response actually reaches the browser before the process starts
        // tearing down - StopApplication begins ASP.NET Core's graceful shutdown, and once Main
        // returns the process exits. What happens after that is entirely up to the host environment
        // (see class doc comment).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            _lifetime.StopApplication();
        });

        return Ok(new SwitchResponse(platform.ToString(), RestartTriggered: true,
            $"Switching to {platform}. The app is restarting - this page will reconnect automatically once it's back."));
    }
}
