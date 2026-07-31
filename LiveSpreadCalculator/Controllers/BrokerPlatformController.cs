using LiveSpreadCalculator.Brokers;
using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Lets the dashboard switch which broker the whole app trades through, entirely in-process - no
/// restart, no downtime. Both platforms' services are always constructed (see BrokerRegistration);
/// this just flips BrokerSwitchState.Current, which every Switching* wrapper reads on its next
/// call. See BrokerSwitchState's own doc comment for the exact choreography (clear ticks, clear
/// spread definitions, stop the outgoing feed, start the new one, resync positions).
/// </summary>
[ApiController]
[Route("api/settings/broker-platform")]
public sealed class BrokerPlatformController : ControllerBase
{
    private readonly BrokerSwitchState _state;

    public BrokerPlatformController(BrokerSwitchState state)
    {
        _state = state;
    }

    public sealed record BrokerPlatformResponse(string Platform);

    /// <summary>The platform actually live right now.</summary>
    [HttpGet]
    public ActionResult<BrokerPlatformResponse> Get() => Ok(new BrokerPlatformResponse(_state.Current.ToString()));

    public sealed record SwitchRequest(string Platform);

    public sealed record SwitchResponse(string Platform, bool Switched, string Message);

    /// <summary>Switches immediately and returns once it's done - by the time this responds, the
    /// new platform is already live (its feed may still be a moment from actually connecting, but
    /// every subsequent API call already routes to it). A no-op (200, Switched:false) when the
    /// requested platform is already the live one.</summary>
    [HttpPost]
    public ActionResult<SwitchResponse> Switch([FromBody] SwitchRequest request)
    {
        if (!Enum.TryParse<BrokerPlatform>(request.Platform, ignoreCase: true, out var platform))
            return BadRequest(new { error = $"Unknown platform '{request.Platform}'. Use 'Kite', 'Dhan', or 'Kotak'." });

        var switched = _state.SwitchTo(platform);

        return Ok(new SwitchResponse(
            platform.ToString(),
            switched,
            switched
                ? $"Switched to {platform}. Live ticks and spread definitions were cleared - rebuild the universe to start streaming again."
                : $"Already running on {platform} - nothing to switch."));
    }
}
