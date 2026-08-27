using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.OptionsFlow;

/// <summary>
/// Drives OptionsFlowEngine on a timer and pushes each refreshed snapshot to connected dashboards.
///
/// Only registered when OptionsFlow:Enabled is true, so with the flag off this type is never
/// constructed and the feature genuinely does not exist at runtime.
///
/// DHAN ONLY, DELIBERATELY. The option chain and the intraday history behind this feature are both
/// Dhan-specific: securityIds are not Kite instrument_tokens, and Kite's historical API is a
/// separately paid add-on this app does not use. Rather than chart meaningless ids while another
/// platform is active, the loop stands down and says so - once, not every cycle - and resumes by
/// itself when the platform is switched back. The trading engine is entirely unaffected either way.
/// </summary>
public sealed class OptionsFlowHostedService : BackgroundService
{
    private readonly OptionsFlowOptions _options;
    private readonly OptionsFlowEngine _engine;
    private readonly IBrokerSwitchNotifier _brokerSwitch;
    private readonly IOptionsFlowPublisher _publisher;
    private readonly ILogger<OptionsFlowHostedService> _logger;

    /// <summary>Tracks whether the "standing down, wrong platform" message has already been logged,
    /// so a long stretch on another broker doesn't fill the log with one line per second.</summary>
    private bool _loggedStandDown;

    public OptionsFlowHostedService(
        IOptions<OptionsFlowOptions> options,
        OptionsFlowEngine engine,
        IBrokerSwitchNotifier brokerSwitch,
        IOptionsFlowPublisher publisher,
        ILogger<OptionsFlowHostedService> logger)
    {
        _options = options.Value;
        _engine = engine;
        _brokerSwitch = brokerSwitch;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _options.LiveRefreshIntervalMs));

        _logger.LogInformation(
            "Options Flow enabled for [{Indices}] - ATM window {Below}/{Above} strikes, re-centred every {Shift} min, refreshing every {Interval} ms.",
            string.Join(", ", _options.ResolveIndices()), _options.StrikesBelowAtm, _options.StrikesAboveAtm,
            _options.AtmShiftIntervalMinutes, interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            if (_brokerSwitch.Current != BrokerPlatform.Dhan)
            {
                if (!_loggedStandDown)
                {
                    _logger.LogWarning(
                        "Options Flow is Dhan-only and the active platform is {Platform} - standing down until Dhan is active again.",
                        _brokerSwitch.Current);
                    _loggedStandDown = true;
                }
                continue;
            }

            _loggedStandDown = false;

            try
            {
                await _engine.RefreshAsync(stoppingToken);

                foreach (var snapshot in _engine.Snapshots.Values)
                    await _publisher.PublishAsync(snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad cycle stop the loop - the next tick recomputes from scratch.
                _logger.LogError(ex, "Options Flow refresh cycle failed - retrying on the next tick.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
