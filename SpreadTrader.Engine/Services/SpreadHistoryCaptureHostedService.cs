using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Captures a snapshot of every spread at fixed IST wall-clock times (default the :29 mark of each
/// trading hour, 09:29-15:29 - see EngineOptions.SpreadHistorySnapshotTimesIst) and bulk-inserts it
/// into the history database (ISpreadHistoryStore). This is a coarse, scheduled record for the
/// "Live Spread" datetime-vs-spread chart and is completely separate from the ~150ms live tick feed
/// (SpreadCalculationHostedService) and the few-minute in-memory ranking (SpreadRankingHostedService)
/// - it is the only path that writes durable per-hour rows.
///
/// Each cycle reads the latest cached value for every spread (ISpreadSnapshotCache), joins it to the
/// spread definition to copy the near/far tradingsymbols and expiries onto the row, stamps a single
/// shared CapturedAt, and writes them all in one AddBatchAsync (bulk insert). On the first capture of
/// each day it also flushes rows whose near leg has expired - the monthly "when a leg expires, flush
/// that leg's data and work on the next legs" rollover (PurgeExpiredAsync).
///
/// Scheduling is done in IST regardless of the host's own timezone (Windows "India Standard Time",
/// falling back to the IANA "Asia/Kolkata" id on Linux/Docker), so the :29 marks line up with the
/// exchange clock on any deployment.
/// </summary>
public sealed class SpreadHistoryCaptureHostedService : BackgroundService
{
    private readonly ISpreadSnapshotCache _snapshotCache;
    private readonly ISpreadDefinitionStore _definitionStore;
    private readonly ISpreadHistoryStore _historyStore;
    private readonly EngineOptions _options;
    private readonly ILogger<SpreadHistoryCaptureHostedService> _logger;
    private readonly TimeZoneInfo _ist;

    private DateOnly _lastPurgeDate = DateOnly.MinValue;

    public SpreadHistoryCaptureHostedService(
        ISpreadSnapshotCache snapshotCache,
        ISpreadDefinitionStore definitionStore,
        ISpreadHistoryStore historyStore,
        IOptions<EngineOptions> options,
        ILogger<SpreadHistoryCaptureHostedService> logger)
    {
        _snapshotCache = snapshotCache;
        _definitionStore = definitionStore;
        _historyStore = historyStore;
        _options = options.Value;
        _logger = logger;
        _ist = ResolveIstTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SpreadHistoryCaptureEnabled)
        {
            _logger.LogInformation("Spread history capture is disabled (Engine:SpreadHistoryCaptureEnabled=false).");
            return;
        }

        var snapshotTimes = ParseSnapshotTimes(_options.SpreadHistorySnapshotTimesIst);
        if (snapshotTimes.Count == 0)
        {
            _logger.LogWarning("No valid Engine:SpreadHistorySnapshotTimesIst configured - history capture will not run.");
            return;
        }

        _logger.LogInformation(
            "Spread history capture scheduled at IST {Times} ({Days}).",
            string.Join(", ", snapshotTimes.Select(t => t.ToString("hh\\:mm"))),
            _options.SpreadHistoryWeekdaysOnly ? "weekdays" : "every day");

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);
            var nextIst = ComputeNextRun(nowIst, snapshotTimes);
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
                await RunCaptureAsync(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spread history capture cycle failed");
            }

            // Nudge past the just-fired minute so ComputeNextRun doesn't re-select the same instant.
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

    private async Task RunCaptureAsync(DateTimeOffset nowIst, CancellationToken cancellationToken)
    {
        if (_options.SpreadHistoryWeekdaysOnly && IsWeekend(nowIst.DayOfWeek))
            return;

        // Once per day, flush rows whose near leg has expired (rollover to the next contracts).
        if (_options.SpreadHistoryPurgeExpiredLegs && _lastPurgeDate != DateOnly.FromDateTime(nowIst.Date))
        {
            _lastPurgeDate = DateOnly.FromDateTime(nowIst.Date);
            try
            {
                await _historyStore.PurgeExpiredAsync(_lastPurgeDate, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Expired-leg purge failed");
            }
        }

        var capturedAt = nowIst; // stamp the whole batch with one IST-offset timestamp
        var snapshots = _snapshotCache.GetAll();
        if (snapshots.Count == 0)
        {
            _logger.LogInformation("Spread history capture at {Time}: no live snapshots cached, nothing to persist.", capturedAt);
            return;
        }

        var points = new List<SpreadHistoryPoint>(snapshots.Count);
        foreach (var s in snapshots)
        {
            var definition = _definitionStore.Get(s.SpreadId);
            points.Add(new SpreadHistoryPoint(
                SpreadId: s.SpreadId,
                Underlying: s.Underlying,
                NearSymbol: definition?.NearLeg.TradingSymbol ?? string.Empty,
                FarSymbol: definition?.FarLeg.TradingSymbol ?? string.Empty,
                NearExpiry: definition?.NearLeg.Expiry,
                FarExpiry: definition?.FarLeg.Expiry,
                CapturedAt: capturedAt,
                NearPrice: s.NearPrice,
                FarPrice: s.FarPrice,
                Direction: s.Direction,
                BuySpreadValue: s.BuySpreadValue,
                SellSpreadValue: s.SellSpreadValue,
                BuySpreadPercent: s.BuySpreadPercent,
                SellSpreadPercent: s.SellSpreadPercent));
        }

        // Persist defensively: a storage failure (DB unreachable, firewall, transient SQL error,
        // etc.) must never bubble up and take down the capture loop or the app. Catch it here, log,
        // and move on - the next scheduled capture will try again.
        try
        {
            await _historyStore.AddBatchAsync(points, cancellationToken);
            _logger.LogInformation("Captured spread history for {Count} spread(s) at IST {Time}.", points.Count, capturedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Spread history bulk insert failed at IST {Time} - {Count} point(s) not persisted; " +
                "application continues and the next scheduled capture will retry.", capturedAt, points.Count);
        }
    }

    /// <summary>The next configured IST time strictly after <paramref name="nowIst"/>, today if one
    /// remains else the first one tomorrow (skipping weekends when configured to).</summary>
    private DateTimeOffset ComputeNextRun(DateTimeOffset nowIst, IReadOnlyList<TimeSpan> snapshotTimes)
    {
        for (var dayOffset = 0; dayOffset < 8; dayOffset++)
        {
            var date = nowIst.Date.AddDays(dayOffset);
            if (_options.SpreadHistoryWeekdaysOnly && IsWeekend(date.DayOfWeek))
                continue;

            foreach (var time in snapshotTimes)
            {
                var candidate = new DateTimeOffset(date + time, nowIst.Offset);
                if (candidate > nowIst)
                    return candidate;
            }
        }

        // Unreachable in practice (there's always a valid slot within 8 days); fall back to +1h.
        return nowIst.AddHours(1);
    }

    private static bool IsWeekend(DayOfWeek day) => day is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private List<TimeSpan> ParseSnapshotTimes(IEnumerable<string> raw)
    {
        var times = new List<TimeSpan>();
        foreach (var entry in raw)
        {
            if (TimeSpan.TryParseExact(entry?.Trim(), "hh\\:mm", null, out var parsed))
                times.Add(parsed);
            else
                _logger.LogWarning("Ignoring invalid snapshot time '{Entry}' (expected HH:mm).", entry);
        }

        return times.Distinct().OrderBy(t => t).ToList();
    }

    private static TimeZoneInfo ResolveIstTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // Last resort: IST is a fixed UTC+05:30 with no DST, so a hand-built zone is exact.
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "IST");
    }
}
