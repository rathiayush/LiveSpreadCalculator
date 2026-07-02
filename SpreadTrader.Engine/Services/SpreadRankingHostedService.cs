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
/// list every 300ms would just flicker.
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
        using var timer = new PeriodicTimer(interval);

        // Publish once immediately on startup too, so the dashboard has a ranking to show
        // straight away rather than waiting a full interval after the process starts.
        await RunCycleAsync(stoppingToken);

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

    private async Task RunCycleAsync(CancellationToken stoppingToken)
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
                UpdatedAt: s.UpdatedAt))
            .ToArray();

        _rankingStore.Set(ranked);

        if (ranked.Length > 0)
            await _publisher.PublishRankingAsync(ranked, stoppingToken);

        _logger.LogDebug("Published spread ranking ({Count} spreads)", ranked.Length);
    }
}
