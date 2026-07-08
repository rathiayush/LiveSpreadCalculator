using Microsoft.Extensions.Hosting;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Realtime;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Streams live PNL for every open NFO position to the Positions &amp; PNL dashboard page over
/// SignalR - not just symbols that happen to be part of a configured spread - instead of that page
/// having to poll GET /api/positions on a timer. See IPositionsPublisher (how updates reach the
/// browser, over the same SignalR hub spreads/triggers already use).
///
/// All the actual work - the periodic Kite REST resync, the order-postback reactivity, and the
/// live-tick PNL/Exit PNL projection - lives in PositionsCache now (shared with
/// PnlAlertHostedService, so there's exactly one poller hitting Kite's positions endpoint, not
/// one per consumer). This class is just PositionsCache.GetProjected() on a timer, gated on
/// PositionsViewerTracker.HasViewers so the SignalR broadcast (and the per-push list rebuild)
/// only happens while at least one connected browser tab actually has the Positions &amp; PNL
/// page open (see SpreadHub.JoinPositions/LeavePositions) - PositionsCache itself keeps resyncing
/// regardless, since PnlAlertHostedService needs it fresh even with no viewers.
/// </summary>
public sealed class LivePositionsHostedService : BackgroundService
{
    // Push cadence for the live-projected PNL - fast enough to feel live, far below what would
    // meaningfully load the browser or SignalR for a handful of position rows. Independent of how
    // often ticks actually arrive (which can be sub-100ms for a busy instrument).
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(750);

    private readonly PositionsCache _positionsCache;
    private readonly IPositionsPublisher _publisher;
    private readonly PositionsViewerTracker _viewerTracker;

    public LivePositionsHostedService(
        PositionsCache positionsCache,
        IPositionsPublisher publisher,
        PositionsViewerTracker viewerTracker)
    {
        _positionsCache = positionsCache;
        _publisher = publisher;
        _viewerTracker = viewerTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Nobody's on Positions & PNL right now - skip the SignalR push entirely. PositionsCache
            // keeps resyncing/subscribing in the background regardless (PnlAlertHostedService still
            // needs it), so the moment a viewer does show up the very next push already has an
            // up-to-date projection ready, with no "just became active" catch-up step needed here.
            if (!_viewerTracker.HasViewers) continue;

            var projected = _positionsCache.GetProjected();
            if (projected.Count == 0) continue;

            await _publisher.PublishAsync(projected, stoppingToken);
        }
    }
}
