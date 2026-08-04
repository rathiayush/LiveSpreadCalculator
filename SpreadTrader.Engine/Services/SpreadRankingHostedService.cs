using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// On a slow timer (RankingIntervalMinutes, default 3), takes the latest snapshot of every
/// spread from ISpreadSnapshotCache, sorts by the absolute value of SpreadPercent descending
/// (biggest carry - in either direction - first), assigns a 1-based Rank, stores the result in
/// ISpreadRankingStore (for GET /api/spreads/ranking) and pushes it to the dashboard as a
/// "rankingUpdate" SignalR event. This is deliberately separate from
/// SpreadCalculationHostedService: that one runs every SpreadRecalculationIntervalMs (e.g.
/// 300ms) and only publishes spreads whose value changed, which is the right cadence for a live
/// tick feed but the wrong cadence for "what should I look at right now" - re-sorting the whole
/// list every 300ms would just flicker. On startup (see WarmUpAsync) it retries every 5 seconds
/// until the first non-empty ranking is available, rather than waiting a full
/// RankingIntervalMinutes for data that's usually ready within seconds.
/// </summary>
public sealed class SpreadRankingHostedService : BackgroundService
{
    private readonly ISpreadSnapshotCache _snapshotCache;
    private readonly ISpreadRankingStore _rankingStore;
    private readonly ISpreadPublisher _publisher;
    private readonly EngineOptions _options;
    private readonly ILogger<SpreadRankingHostedService> _logger;

    public SpreadRankingHostedService(
        ISpreadSnapshotCache snapshotCache,
        ISpreadRankingStore rankingStore,
        ISpreadPublisher publisher,
        IOptions<EngineOptions> options,
        ILogger<SpreadRankingHostedService> logger)
    {
        _snapshotCache = snapshotCache;
        _rankingStore = rankingStore;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(0.1, _options.RankingIntervalMinutes));

        await WarmUpAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spread ranking cycle failed");
            }
        }
    }

    /// <summary>
    /// Right after startup (or right after a fresh universe build) ISpreadSnapshotCache is
    /// empty - no ticks have arrived yet - so the very first cycle publishes nothing. Without
    /// this, the next attempt wouldn't happen until a full RankingIntervalMinutes later (default
    /// 3 minutes), even though real snapshot data is usually ready within a few seconds. Instead,
    /// retry on a short interval until the first non-empty ranking is produced, capped at 2 minutes
    /// so a genuinely quiet period (market closed, nothing subscribed yet) doesn't spin forever -
    /// the normal timer loop picks it up from there regardless.
    ///
    /// The retry interval is deliberately short (1s) so ranking appears almost as soon as the first
    /// spreads have real values, instead of trailing them by up to 5 seconds. A 1s in-memory
    /// sort+publish is cheap, and this loop only runs during the brief warm-up window (it exits the
    /// instant the first non-empty ranking goes out), so the tighter poll costs nothing once the
    /// feed is live.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken stoppingToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var retryInterval = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool published;
            try
            {
                published = await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spread ranking warm-up cycle failed");
                published = false;
            }

            if (published || DateTimeOffset.UtcNow >= deadline)
                return;

            try
            {
                await Task.Delay(retryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RunCycleAsync(CancellationToken stoppingToken)
    {
        var ranked = _snapshotCache.GetAll()
            .OrderByDescending(s => Math.Abs(s.SpreadPercent))
            .Select((s, index) => new SpreadRanking(
                Rank: index + 1,
                SpreadId: s.SpreadId,
                Underlying: s.Underlying,
                NearPrice: s.NearPrice,
                FarPrice: s.FarPrice,
                Direction: s.Direction,
                SpreadValue: s.SpreadValue,
                SpreadPercent: s.SpreadPercent,
                UpdatedAt: s.UpdatedAt,
                SpotPrice: s.SpotPrice,
                BasisPercent: s.BasisPercent))
            .ToArray();

        _rankingStore.Set(ranked);

        if (ranked.Length > 0)
            await _publisher.PublishRankingAsync(ranked, stoppingToken);

        _logger.LogDebug("Published spread ranking ({Count} spreads)", ranked.Length);

        return ranked.Length > 0;
    }
}
