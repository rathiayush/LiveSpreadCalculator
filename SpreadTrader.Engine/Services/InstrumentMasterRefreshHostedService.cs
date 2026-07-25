using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Refreshes the broker instrument master once a day at a fixed IST time (default 06:00 - see
/// EngineOptions.InstrumentMasterRefreshTimeIst), the same scheduling idiom
/// DailyMaintenanceHostedService already uses, so no live request is ever the one that pays for a
/// stale-cache reload.
///
/// WHY THIS EXISTS. Both instrument masters (Dhan's DhanInstrumentMasterService, Kite's
/// InstrumentMasterService) cache their scrip dump for 12 hours and reload lazily otherwise - the
/// FIRST caller after that window expires pays the full download+parse cost inline. On Dhan that
/// dump is one flat ~26 MB file covering every exchange and instrument type (there's no
/// exchange-scoped endpoint), and several live, frequently-hit endpoints call EnsureLoadedAsync
/// directly in their request path: GET /api/instruments/futures* (the Add Spread pickers),
/// GET /api/positions, and POST /api/spreads. Whichever request happens to land the moment the
/// cache goes stale eats a multi-second download it did nothing to deserve.
///
/// This app mostly trades monthly F&O contracts, which don't need intraday-fresh listings - once
/// a day, ahead of the market open, is enough. DhanInstrumentMasterService additionally persists
/// the downloaded scrip master to disk and warms its in-memory index from that file on the very
/// first call after a process restart, so a redeploy no longer means waiting on a fresh download
/// before anything can resolve a symbol either - see its EnsureLoadedAsync doc comment.
///
/// Broker-agnostic on purpose: depends on IBrokerInstrumentMaster only, so it works unchanged
/// whichever broker is configured.
/// </summary>
public sealed class InstrumentMasterRefreshHostedService : BackgroundService
{
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly EngineOptions _options;
    private readonly ILogger<InstrumentMasterRefreshHostedService> _logger;
    private readonly TimeZoneInfo _ist;

    public InstrumentMasterRefreshHostedService(
        IBrokerInstrumentMaster instrumentMaster,
        IOptions<EngineOptions> options,
        ILogger<InstrumentMasterRefreshHostedService> logger)
    {
        _instrumentMaster = instrumentMaster;
        _options = options.Value;
        _logger = logger;
        _ist = ResolveIstTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TimeSpan.TryParseExact(_options.InstrumentMasterRefreshTimeIst?.Trim(), "hh\\:mm", null, out var runAt))
        {
            _logger.LogWarning(
                "Invalid Engine:InstrumentMasterRefreshTimeIst '{Value}' (expected HH:mm) - the daily instrument " +
                "master refresh will not run (the 12h lazy cache in each instrument master still applies).",
                _options.InstrumentMasterRefreshTimeIst);
            return;
        }

        _logger.LogInformation("Instrument master refresh scheduled for IST {Time} each day.", runAt.ToString("hh\\:mm"));

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);
            var nextIst = ComputeNextRun(nowIst, runAt);
            var delay = nextIst - nowIst;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            if (!await DelayAsync(delay, stoppingToken)) return;

            try
            {
                await _instrumentMaster.EnsureLoadedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never fatal: whatever was last successfully loaded stays in place (both
                // implementations only swap in new data after a successful parse), so a failed
                // refresh costs nothing beyond staying on slightly older data until tomorrow's run.
                _logger.LogWarning(ex, "Daily instrument master refresh failed - keeping the previously loaded data.");
            }

            // Move past the just-fired minute so ComputeNextRun rolls to tomorrow rather than
            // firing again immediately.
            if (!await DelayAsync(TimeSpan.FromSeconds(61), stoppingToken)) return;
        }
    }

    /// <summary>The configured IST time today if it's still ahead, otherwise tomorrow.</summary>
    private static DateTimeOffset ComputeNextRun(DateTimeOffset nowIst, TimeSpan runAt)
    {
        var todayRun = new DateTimeOffset(nowIst.Date + runAt, nowIst.Offset);
        return todayRun > nowIst ? todayRun : todayRun.AddDays(1);
    }

    private static TimeZoneInfo ResolveIstTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // IST is a fixed UTC+05:30 with no DST, so a hand-built zone is exact.
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "IST");
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
