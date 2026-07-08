using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Positions;
using SpreadTrader.KiteIntegration.Ticker;

namespace SpreadTrader.Engine.Services;

/// <summary>A REST position snapshot paired with the instrument token it prices off of -
/// PositionSnapshot itself deliberately doesn't carry this (it's not part of the public GET
/// /api/positions contract), so PositionsCache tracks it alongside instead.</summary>
public readonly record struct PositionAnchor(PositionSnapshot Snapshot, uint InstrumentToken);

/// <summary>
/// The ONE place that periodically re-fetches Kite's authoritative position snapshot (GET
/// /portfolio/positions via KitePositionsService) and keeps it in memory - every other consumer
/// that needs "what positions are currently open, with a live-tick-projected PNL/Exit PNL" reads
/// from here via GetProjected() instead of calling Kite itself. Previously LivePositionsHostedService
/// owned this resync loop privately; when PnlAlertHostedService needed the same data, the naive
/// approach would have been a second independent resync loop hitting the same rate-limited REST
/// endpoint on its own timer, duplicating work and risking the two ending up with slightly
/// different numbers. This extraction avoids both problems - one poller, one cache, N readers.
///
/// Runs unconditionally from process start - deliberately NOT gated on
/// PositionsViewerTracker.HasViewers the way the live SignalR push in LivePositionsHostedService
/// still is. A PNL alert (PnlAlertHostedService) needs this fresh even when nobody has the
/// Positions &amp; PNL page open; only the "broadcast to a browser tab" step has any reason to
/// skip work when there's no browser tab to broadcast to.
///
/// Subscribes every open position's instrument token on the live ticker (not just symbols that
/// happen to be part of a configured spread, unlike TickIngestionHostedService) so GetProjected()
/// can price ANY open position off a live tick, not just spread legs.
/// </summary>
public sealed class PositionsCache : BackgroundService
{
    // Fallback full REST re-sync, in case an order postback is somehow missed (a brief WebSocket
    // hiccup, or Kite's postback delivery itself failing) - keeps this from silently drifting
    // stale indefinitely even if the reactive path fails.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(20);

    // How often the loop wakes up just to check "did an order postback arrive" / "has
    // ResyncInterval elapsed" - deliberately shorter than ResyncInterval itself so a postback
    // (which should trigger an immediate resync) isn't sitting unnoticed for up to 20s.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly KitePositionsService _positionsService;
    private readonly KiteTickerClient _tickerClient;
    private readonly ITickStore _tickStore;
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly ILogger<PositionsCache> _logger;

    private readonly object _lock = new();
    private IReadOnlyDictionary<string, PositionAnchor> _anchors = new Dictionary<string, PositionAnchor>();

    // Set (from the ticker's receive loop, via OnOrderUpdate) whenever an order postback arrives;
    // read and cleared by the main loop below - see LivePositionsHostedService's old doc comment on
    // this same pattern, unchanged by the extraction.
    private int _orderUpdatePending;

    public PositionsCache(
        KitePositionsService positionsService,
        KiteTickerClient tickerClient,
        ITickStore tickStore,
        InstrumentMasterService instrumentMaster,
        ILogger<PositionsCache> logger)
    {
        _positionsService = positionsService;
        _tickerClient = tickerClient;
        _tickStore = tickStore;
        _instrumentMaster = instrumentMaster;
        _logger = logger;
    }

    /// <summary>Every currently-anchored position, each with LastPrice/Pnl/ExitPrice/ExitPnl
    /// freshly recomputed from whatever tick is currently in ITickStore for its instrument -
    /// exactly the projection LivePositionsHostedService used to do inline (see PNL(t) =
    /// PnlAtSnapshot + Quantity * (CurrentLtp - LtpAtSnapshot) and PositionExitPricing.Compute),
    /// now shared so every reader gets an identically-computed, up-to-the-tick figure instead of
    /// the REST snapshot's up-to-ResyncInterval-old one. Empty if nothing is open.</summary>
    public IReadOnlyList<PositionSnapshot> GetProjected()
    {
        IReadOnlyDictionary<string, PositionAnchor> anchors;
        lock (_lock) { anchors = _anchors; }

        if (anchors.Count == 0) return Array.Empty<PositionSnapshot>();

        var projected = new List<PositionSnapshot>(anchors.Count);
        foreach (var anchor in anchors.Values)
        {
            var snapshot = anchor.Snapshot;

            // tick is pre-declared (rather than `out var tick` inline in the && below) so it's
            // unconditionally definitely-assigned at every later use point - the compiler only
            // tracks definite-assignment for an out-var through the exact && expression that
            // declares it, not through a separate `hasTick` bool copy used later (CS0165).
            var tick = default(Tick);
            var hasTick = anchor.InstrumentToken != 0 && _tickStore.TryGet(anchor.InstrumentToken, out tick);

            var currentLtp = snapshot.LastPrice;
            if (hasTick && tick.LastPrice > 0)
                currentLtp = tick.LastPrice;

            var projectedPnl = snapshot.Pnl + snapshot.Quantity * (currentLtp - snapshot.LastPrice);
            var (exitPrice, exitPnl) = PositionExitPricing.Compute(snapshot.Quantity, snapshot.AveragePrice, hasTick ? tick : null);

            projected.Add(snapshot with { LastPrice = currentLtp, Pnl = projectedPnl, ExitPrice = exitPrice, ExitPnl = exitPnl });
        }

        return projected;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tickerClient.OrderUpdateReceived += OnOrderUpdate;
        try
        {
            // Unlike the old viewer-gated LivePositionsHostedService (which deliberately waited
            // for the first viewer before spending a REST call), this resyncs once right at
            // startup - PnlAlertHostedService and the live page can both need this before anyone's
            // opened a browser tab at all.
            await ResyncAsync(stoppingToken);
            var lastResync = DateTimeOffset.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CheckInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var orderUpdatePending = Interlocked.Exchange(ref _orderUpdatePending, 0) == 1;
                if (orderUpdatePending || DateTimeOffset.UtcNow - lastResync > ResyncInterval)
                {
                    await ResyncAsync(stoppingToken);
                    lastResync = DateTimeOffset.UtcNow;
                }
            }
        }
        finally
        {
            _tickerClient.OrderUpdateReceived -= OnOrderUpdate;
        }
    }

    private void OnOrderUpdate() => Interlocked.Exchange(ref _orderUpdatePending, 1);

    /// <summary>Re-fetches the authoritative REST snapshot (quantity, average price, Kite's own
    /// pnl, last_price at fetch time), rebuilds the anchor for every open position, and makes
    /// sure each one's instrument token is subscribed on the live ticker. A failed fetch (no
    /// access token yet, transient API error) just logs and keeps whatever anchors already exist -
    /// a stale-but-recent snapshot is more useful than blanking everything out.</summary>
    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        var result = await _positionsService.GetPositionsAsync(cancellationToken);
        if (!result.Success)
        {
            _logger.LogDebug("PositionsCache: REST resync failed ({Error}) - keeping previous snapshot.", result.ErrorMessage);
            return;
        }

        var newAnchors = new Dictionary<string, PositionAnchor>();
        var tokensToSubscribe = new List<uint>();

        foreach (var position in result.Positions)
        {
            // Already resolved/cached by KitePositionsService.GetPositionsAsync itself (it calls
            // InstrumentMasterService.EnsureLoadedAsync before resolving LotSize per position),
            // so this is a plain in-memory lookup, not a fresh network round trip.
            var instrument = _instrumentMaster.Find(position.TradingSymbol, position.Exchange);
            var token = instrument?.InstrumentToken ?? 0;

            newAnchors[AnchorKey(position.TradingSymbol, position.Exchange, position.Product)] = new PositionAnchor(position, token);
            if (token != 0) tokensToSubscribe.Add(token);
        }

        lock (_lock)
        {
            _anchors = newAnchors;
        }

        if (tokensToSubscribe.Count > 0)
            await _tickerClient.SubscribeAsync(tokensToSubscribe, cancellationToken);
    }

    private static string AnchorKey(string tradingSymbol, string exchange, string product) =>
        $"{tradingSymbol}|{exchange}|{product}";
}
