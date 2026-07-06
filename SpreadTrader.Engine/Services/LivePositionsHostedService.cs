using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Realtime;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Positions;
using SpreadTrader.KiteIntegration.Ticker;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Streams live PNL for every open NFO position - not just symbols that happen to be part of a
/// configured spread - instead of the Positions & PNL dashboard page having to poll GET
/// /api/positions on a timer. See KitePositionsService (the plain REST snapshot this builds on)
/// and IPositionsPublisher (how updates reach the browser, over the same SignalR hub spreads/
/// triggers already use).
///
/// Kite doesn't offer a "PNL" push of its own - a position's pnl is derived (quantity times the
/// gap between LTP and average price, plus realised/day-trade terms), not something Kite computes
/// and streams. This combines two things Kite DOES stream over the ticker WebSocket to get an
/// exact (not approximate) live figure between REST snapshots:
///
///  1. Live LTP ticks - every open position's instrument is subscribed here (via
///     KiteTickerClient.SubscribeAsync) even if no configured spread uses it, unlike
///     TickIngestionHostedService, which only cares about spread legs. Reads come from the same
///     shared ITickStore that service already keeps updated for every subscribed token.
///  2. Order postbacks (KiteTickerClient.OrderUpdateReceived) - fired the instant ANY order for
///     this account changes status, from this app or anywhere else (Kite's own docs recommend
///     this exact mechanism for single-user apps - see
///     https://kite.trade/docs/connect/v3/websocket/#postbacks-and-non-binary-updates). Used as
///     the cue to re-fetch the REST snapshot, since a fill is the only thing that changes
///     Quantity/AveragePrice - ticks alone can't tell us that happened.
///
/// The REST resync and SignalR push below only run while PositionsViewerTracker.HasViewers is
/// true - i.e. while at least one connected browser tab actually has the Positions &amp; PNL page
/// open (see SpreadHub.JoinPositions/LeavePositions). Previously this ran unconditionally for the
/// whole app session regardless of which page anyone was looking at, which meant a Kite REST call
/// roughly every 20s and a SignalR broadcast roughly every 750ms even when nobody was watching -
/// wasted API rate-limit budget and network/CPU for no benefit. This loop still ticks every
/// PushInterval to check the flag cheaply, but does nothing else while it's false.
///
/// Between REST snapshots, each position's PNL is "anchored" and projected forward:
/// PNL(t) = PnlAtSnapshot + Quantity * (CurrentLtp - LtpAtSnapshot). This is exact, not an
/// estimate - PNL is linear in LTP for a fixed Quantity, so Quantity is precisely PNL's
/// derivative with respect to price, which is all that's needed to project it between snapshots
/// without reverse-engineering Kite's exact realised/unrealised split. Every resync (postback-
/// triggered, or the periodic fallback below) resets the anchor to Kite's own authoritative
/// figures, so no drift can accumulate.
/// </summary>
public sealed class LivePositionsHostedService : BackgroundService
{
    // Push cadence for the live-projected PNL - fast enough to feel live, far below what would
    // meaningfully load the browser or SignalR for a handful of position rows. Independent of how
    // often ticks actually arrive (which can be sub-100ms for a busy instrument).
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(750);

    // Fallback full REST re-sync, in case an order postback is somehow missed (a brief WebSocket
    // hiccup, or Kite's postback delivery itself failing) - keeps this from silently drifting
    // stale indefinitely even if the reactive path fails.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(20);

    private readonly KitePositionsService _positionsService;
    private readonly KiteTickerClient _tickerClient;
    private readonly ITickStore _tickStore;
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly IPositionsPublisher _publisher;
    private readonly PositionsViewerTracker _viewerTracker;
    private readonly ILogger<LivePositionsHostedService> _logger;

    private readonly object _anchorsLock = new();
    private Dictionary<string, PositionAnchor> _anchors = new();

    // Set (from the ticker's receive loop, via OnOrderUpdate) whenever an order postback arrives;
    // read and cleared by the main loop below. A plain int/Interlocked flag rather than an event
    // or extra synchronization primitive - the main loop already wakes up every PushInterval
    // regardless, so a flag it checks each time is all a cross-thread "something changed" signal
    // needs to be here. Left set (not cleared) while there are no viewers - see ExecuteAsync,
    // which only clears it once it actually acts on it - so a fill that happens while nobody's
    // watching still triggers an immediate resync the moment someone opens the page.
    private int _orderUpdatePending;

    public LivePositionsHostedService(
        KitePositionsService positionsService,
        KiteTickerClient tickerClient,
        ITickStore tickStore,
        InstrumentMasterService instrumentMaster,
        IPositionsPublisher publisher,
        PositionsViewerTracker viewerTracker,
        ILogger<LivePositionsHostedService> logger)
    {
        _positionsService = positionsService;
        _tickerClient = tickerClient;
        _tickStore = tickStore;
        _instrumentMaster = instrumentMaster;
        _publisher = publisher;
        _viewerTracker = viewerTracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tickerClient.OrderUpdateReceived += OnOrderUpdate;
        try
        {
            // Deliberately no resync/push here before the loop starts (unlike the old version) -
            // there's no point spending a Kite REST call at process startup before anyone's even
            // opened the page. The loop's own "just became active" check below does the first
            // resync as soon as a viewer actually shows up.
            var lastResync = DateTimeOffset.MinValue;
            var wasActive = false;

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

                if (!_viewerTracker.HasViewers)
                {
                    // Nobody's on Positions & PNL right now - skip the REST resync AND the
                    // SignalR push entirely. wasActive is reset here so the next viewer to show
                    // up is treated as "just became active" and gets an immediate resync rather
                    // than waiting out whatever was left of the old ResyncInterval countdown.
                    wasActive = false;
                    continue;
                }

                var justBecameActive = !wasActive;
                wasActive = true;

                var orderUpdatePending = Interlocked.Exchange(ref _orderUpdatePending, 0) == 1;
                if (justBecameActive || orderUpdatePending || DateTimeOffset.UtcNow - lastResync > ResyncInterval)
                {
                    await ResyncAsync(stoppingToken);
                    lastResync = DateTimeOffset.UtcNow;
                }

                await PushProjectedAsync(stoppingToken);
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
    /// sure each one's instrument token is subscribed on the live ticker - including symbols no
    /// configured spread streams, which is the whole point of this service existing separately
    /// from TickIngestionHostedService. A failed fetch (no access token yet, transient API
    /// error) just logs and keeps whatever anchors already exist - a stale-but-recent projection
    /// is more useful than blanking the page out.</summary>
    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        var result = await _positionsService.GetPositionsAsync(cancellationToken);
        if (!result.Success)
        {
            _logger.LogDebug("LivePositionsHostedService: REST resync failed ({Error}) - keeping previous anchors.", result.ErrorMessage);
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

        lock (_anchorsLock)
        {
            _anchors = newAnchors;
        }

        if (tokensToSubscribe.Count > 0)
            await _tickerClient.SubscribeAsync(tokensToSubscribe, cancellationToken);
    }

    /// <summary>Projects each anchored position's PNL forward using the latest tick (if one has
    /// arrived for its instrument token) and publishes the result. A no-op if there are no open
    /// positions at all, rather than pushing an empty list on every tick when the account is
    /// flat.</summary>
    private Task PushProjectedAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, PositionAnchor> anchors;
        lock (_anchorsLock)
        {
            anchors = _anchors;
        }

        if (anchors.Count == 0) return Task.CompletedTask;

        var projected = new List<PositionSnapshot>(anchors.Count);
        foreach (var anchor in anchors.Values)
        {
            var snapshot = anchor.Snapshot;
            var currentLtp = snapshot.LastPrice;
            if (anchor.InstrumentToken != 0 && _tickStore.TryGet(anchor.InstrumentToken, out var tick) && tick.LastPrice > 0)
                currentLtp = tick.LastPrice;

            // Exact, not approximate - see the class doc comment on why Quantity is PNL's
            // derivative w.r.t. price for an unchanged position.
            var projectedPnl = snapshot.Pnl + snapshot.Quantity * (currentLtp - snapshot.LastPrice);
            projected.Add(snapshot with { LastPrice = currentLtp, Pnl = projectedPnl });
        }

        return _publisher.PublishAsync(projected, cancellationToken);
    }

    private static string AnchorKey(string tradingSymbol, string exchange, string product) =>
        $"{tradingSymbol}|{exchange}|{product}";

    /// <summary>A REST position snapshot paired with the instrument token it prices off of -
    /// PositionSnapshot itself deliberately doesn't carry this (it's not part of the public GET
    /// /api/positions contract), so it's tracked alongside instead.</summary>
    private readonly record struct PositionAnchor(PositionSnapshot Snapshot, uint InstrumentToken);
}
