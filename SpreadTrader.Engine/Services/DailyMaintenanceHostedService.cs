using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Runs once every day at a fixed IST time (default 06:00, before the 09:15 open - see
/// EngineOptions.DailyMaintenanceTimeIst) and resets the app's day-to-day state:
///   - clears ALL entry trigger rules (ITriggerRuleStore.RemoveAll),
///   - clears ALL exit trigger rules (IExitTriggerRuleStore.RemoveAll),
///   - clears the in-memory trigger activity feed (ITriggerActivityStore.Clear),
///   - deletes spread-history rows older than SpreadHistoryRetentionDays
///     (ISpreadHistoryStore.PurgeOlderThanAsync) - an age-based retention cap on the history table
///     that's independent of the per-leg expiry flush in SpreadHistoryCaptureHostedService.
///
/// Each step is independently toggleable in config and each is wrapped so one failing doesn't stop
/// the others. Scheduling is done against the IST wall clock regardless of the host timezone (same
/// approach as SpreadHistoryCaptureHostedService), so it fires at the intended local time on any
/// deployment.
/// </summary>
public sealed class DailyMaintenanceHostedService : BackgroundService
{
    private readonly ITriggerRuleStore _triggerStore;
    private readonly IExitTriggerRuleStore _exitTriggerStore;
    private readonly ITriggerActivityStore _activityStore;
    private readonly ISpreadHistoryStore _historyStore;
    private readonly EngineOptions _options;
    private readonly ILogger<DailyMaintenanceHostedService> _logger;
    private readonly TimeZoneInfo _ist;

    public DailyMaintenanceHostedService(
        ITriggerRuleStore triggerStore,
        IExitTriggerRuleStore exitTriggerStore,
        ITriggerActivityStore activityStore,
        ISpreadHistoryStore historyStore,
        IOptions<EngineOptions> options,
        ILogger<DailyMaintenanceHostedService> logger)
    {
        _triggerStore = triggerStore;
        _exitTriggerStore = exitTriggerStore;
        _activityStore = activityStore;
        _historyStore = historyStore;
        _options = options.Value;
        _logger = logger;
        _ist = ResolveIstTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DailyMaintenanceEnabled)
        {
            _logger.LogInformation("Daily maintenance job is disabled (Engine:DailyMaintenanceEnabled=false).");
            return;
        }

        if (!TimeSpan.TryParseExact(_options.DailyMaintenanceTimeIst?.Trim(), "hh\\:mm", null, out var runAt))
        {
            _logger.LogWarning(
                "Invalid Engine:DailyMaintenanceTimeIst '{Value}' (expected HH:mm) - daily maintenance will not run.",
                _options.DailyMaintenanceTimeIst);
            return;
        }

        _logger.LogInformation("Daily maintenance scheduled for IST {Time} each day.", runAt.ToString("hh\\:mm"));

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);
            var nextIst = ComputeNextRun(nowIst, runAt);
            var delay = nextIst - nowIst;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance cycle failed");
            }

            // Move past the just-fired minute so ComputeNextRun rolls to tomorrow.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(61), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        // Skip weekends (market closed) unless explicitly told to run every day.
        var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);
        if (_options.DailyMaintenanceWeekdaysOnly && IsWeekend(nowIst.DayOfWeek))
        {
            _logger.LogInformation("Daily maintenance skipped ({Day}) - weekends are off.", nowIst.DayOfWeek);
            return;
        }

        _logger.LogInformation("Running daily maintenance.");

        if (_options.DailyMaintenanceClearTriggers)
        {
            try
            {
                var removed = _triggerStore.RemoveAll();
                _logger.LogInformation("Daily maintenance: cleared {Count} entry trigger rule(s).", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance: clearing entry triggers failed");
            }
        }

        if (_options.DailyMaintenanceClearExitTriggers)
        {
            try
            {
                var removed = _exitTriggerStore.RemoveAll();
                _logger.LogInformation("Daily maintenance: cleared {Count} exit trigger rule(s).", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance: clearing exit triggers failed");
            }
        }

        if (_options.DailyMaintenanceClearTriggerActivity)
        {
            try
            {
                _activityStore.Clear();
                _logger.LogInformation("Daily maintenance: cleared the trigger activity feed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance: clearing trigger activity failed");
            }
        }

        // The history table is shared and single-writer: only the writer instance deletes from it.
        // Reader instances still clear their own (local) triggers/activity above, but never touch the DB.
        if (_options.SpreadHistoryWriter && _options.SpreadHistoryRetentionDays > 0)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.SpreadHistoryRetentionDays);
                var removed = await _historyStore.PurgeOlderThanAsync(cutoff, cancellationToken);
                _logger.LogInformation(
                    "Daily maintenance: deleted {Count} spread history row(s) older than {Days} day(s).",
                    removed, _options.SpreadHistoryRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance: spread history retention purge failed");
            }
        }
    }

    private static bool IsWeekend(DayOfWeek day) => day is DayOfWeek.Saturday or DayOfWeek.Sunday;

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
}
