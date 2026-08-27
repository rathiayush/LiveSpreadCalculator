using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>A REST position snapshot paired with the instrument token it prices off of -
/// PositionSnapshot itself deliberately doesn't carry this (it's not part of the public GET
/// /api/positions contract), so PositionsCache tracks it alongside instead.</summary>
public readonly record struct PositionAnchor(PositionSnapshot Snapshot, uint InstrumentToken);

/// <summary>
/// The ONE place that re-fetches the broker's authoritative position snapshot (GET positions via
/// IPositionsService) and keeps it in memory - every other consumer that needs "what positions
/// are currently open, with a live-tick-projected PNL/Exit PNL" reads from here via
/// GetProjected() instead of calling the broker itself. Previously LivePositionsHostedService
/// owned this resync loop privately; when PnlAlertHostedService needed the same data, the naive
/// approach would have been a second independent resync loop hitting the same rate-limited REST
/// endpoint on its own timer, duplicating work and risking the two ending up with slightly
/// different numbers. This extraction avoids both problems - one fetcher, one cache, N readers.
///
/// The resync is EVENT-DRIVEN, not polled. There are exactly two things that fetch positions:
///
///  1. STARTUP, retried with backoff until the first snapshot lands. The retry costs no API
///     calls while logged out - IPositionsService returns "no access token" without touching the
///     wire - and is what makes the cache come alive on its own once the operator completes the
///     broker login, without waiting for the first trade.
///  2. AN ORDER UPDATE from the broker's socket (OrderUpdateReceived), the only thing that can
///     change quantity, average price or realised PNL. A burst of alerts from one multi-leg
///     basket coalesces into a single fetch (see CoalesceWindow), and a fetch that fails right
///     after an execution retries with backoff until it succeeds - stale anchors after a REAL
///     fill are the one state this cache must never sit in.
///
/// There is deliberately NO periodic timer resync. The accepted trade-off: if an order-update
/// message is lost outright (socket reconnecting at the exact moment a trade placed directly in
/// the broker's own app executes), the cache stays stale until the next order event - the
/// Positions page's manual refresh (GET /api/positions, which calls the broker directly) is the
/// escape hatch. Between fetches, PNL freshness comes from the tick stream, not from here -
/// see GetProjected().
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
    // How long to sit on an order-update signal before fetching, so the burst of alerts one
    // basket produces (two legs placed, each traversing TRANSIT/PENDING/TRADED) lands as ONE
    // positions fetch instead of one per alert. Order events arriving during the window - or
    // during the fetch itself - simply arm the next round, so nothing is ever dropped.
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(300);

    // Backoff for a resync that FAILED (startup before login, or a network/API hiccup right
    // after an execution). Grows exponentially to the cap, resets on success.
    private static readonly TimeSpan RetryBackoffInitial = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryBackoffMax = TimeSpan.FromSeconds(30);

    private readonly IPositionsService _positionsService;
    private readonly IMarketDataFeed _tickerClient;
    private readonly ITickStore _tickStore;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly IBrokerSwitchNotifier? _brokerSwitchNotifier;
    private readonly ILogger<PositionsCache> _logger;

    private readonly object _lock = new();
    private IReadOnlyDictionary<string, PositionAnchor> _anchors = new Dictionary<string, PositionAnchor>();

    // Order-update signalling: OnOrderUpdate (called on the ticker's receive loop, which must
    // never block) flips the flag and releases the semaphore exactly once per pending round; the
    // main loop blocks on the semaphore, so between order events it consumes nothing at all -
    // no timer, no wake-ups, no API calls. The flag is what coalesces a burst of alerts into one
    // release: while it is already 1, further alerts are no-ops that ride along with the fetch
    // the first alert armed. A broker platform switch (see IBrokerSwitchNotifier) is signalled
    // through this exact same path - as far as this cache is concerned, "the active platform just
    // changed" and "an order just changed" both mean the same thing: re-fetch now.
    private readonly SemaphoreSlim _resyncSignal = new(0, 1);
    private int _orderUpdatePending;

    public PositionsCache(
        IPositionsService positionsService,
        IMarketDataFeed tickerClient,
        ITickStore tickStore,
        IBrokerInstrumentMaster instrumentMaster,
        ILogger<PositionsCache> logger,
        IBrokerSwitchNotifier? brokerSwitchNotifier = null)
    {
        _positionsService = positionsService;
        _tickerClient = tickerClient;
        _tickStore = tickStore;
        _instrumentMaster = instrumentMaster;
        _logger = logger;
        _brokerSwitchNotifier = brokerSwitchNotifier;
    }

    /// <summary>Every currently-anchored position, each with LastPrice/Pnl/ExitPrice/ExitPnl
    /// freshly recomputed from whatever tick is currently in ITickStore for its instrument -
    /// exactly the projection LivePositionsHostedService used to do inline (see PNL(t) =
    /// PnlAtSnapshot + Quantity * (CurrentLtp - LtpAtSnapshot) and PositionExitPricing.Compute),
    /// now shared so every reader gets an identically-computed, up-to-the-tick figure instead of
    /// the REST snapshot's as-of-last-order-event one. Empty if nothing is open.</summary>
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

            var projectedPnl = ProjectPnl(snapshot, currentLtp);
            var (exitPrice, exitPnl) = PositionExitPricing.Compute(snapshot.Quantity, snapshot.AveragePrice, hasTick ? tick : null);

            projected.Add(snapshot with { LastPrice = currentLtp, Pnl = projectedPnl, ExitPrice = exitPrice, ExitPnl = exitPnl });
        }

        return projected;
    }

    /// <summary>
    /// Live PNL for one position at <paramref name="currentLtp"/>. Two ways to get there, and which
    /// one is available depends on what the broker actually told us.
    ///
    /// FROM FIRST PRINCIPLES (preferred - used when the broker separates realised PNL, i.e. Dhan):
    ///     realised + quantity * (live price - average price)
    /// Everything on the right is either fixed for the life of the position or comes straight off
    /// the tick stream, so this needs no reference price from the broker at all and is exact at
    /// every tick.
    ///
    /// BY DELTA (used when the broker reports one combined figure, i.e. Kite):
    ///     broker's PNL at snapshot + quantity * (live price - price the snapshot was struck at)
    /// This is only valid while <c>LastPrice</c> really is the price the broker computed that PNL
    /// at. Kite returns exactly that on the position row, so it holds there.
    ///
    /// It did NOT hold on Dhan, which returns no last-traded-price on a position at all - the
    /// adapter fills LastPrice from the local tick store, leaving it at 0 for any position whose
    /// first tick hasn't arrived yet. A 0 baseline turns the delta term into
    /// quantity * live price, i.e. it adds the position's ENTIRE NOTIONAL to PNL: ~7.5 lakh of
    /// phantom profit on 500 RELIANCE, shown on the Positions and PNL page and fed to
    /// PnlAlertHostedService, which fires real alerts off these numbers. That window opened on
    /// every restart and every newly-opened position, and lasted until the next resync.
    ///
    /// Hence the explicit guard below: with no usable baseline the honest answer is the broker's
    /// own snapshot figure, not a projection off a price we never had.
    /// </summary>
    private static decimal ProjectPnl(PositionSnapshot snapshot, decimal currentLtp)
    {
        if (currentLtp <= 0) return snapshot.Pnl;

        if (snapshot.RealizedPnl is { } realized && snapshot.AveragePrice > 0)
            return realized + snapshot.Quantity * (currentLtp - snapshot.AveragePrice);

        if (snapshot.LastPrice <= 0) return snapshot.Pnl;

        return snapshot.Pnl + snapshot.Quantity * (currentLtp - snapshot.LastPrice);
    }

    /// <summary>Instrument tokens for every currently-anchored (open) position - read by
    /// TickIngestionHostedService's reconciliation loop, alongside every configured spread leg's
    /// token, to decide what the live ticker still actually needs subscribed. A closed position
    /// simply stops appearing here on the next ResyncAsync (its anchor is dropped, see below),
    /// which is what lets that token be unsubscribed if nothing else needs it either.</summary>
    public IReadOnlyCollection<uint> GetAnchoredTokens()
    {
        IReadOnlyDictionary<string, PositionAnchor> anchors;
        lock (_lock) { anchors = _anchors; }
        return anchors.Values.Where(a => a.InstrumentToken != 0).Select(a => a.InstrumentToken).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tickerClient.OrderUpdateReceived += OnOrderUpdate;
        if (_brokerSwitchNotifier is not null) _brokerSwitchNotifier.Changed += OnBrokerSwitched;
        try
        {
            // STARTUP: retry until the first snapshot lands. Costs no API calls while logged out
            // (the positions service early-returns on a missing token), and means completing the
            // broker login is enough to populate the cache - without this, a purely event-driven
            // loop would show no positions until the first trade of the day.
            var backoff = RetryBackoffInitial;
            while (!stoppingToken.IsCancellationRequested && !await ResyncAsync(stoppingToken))
            {
                if (!await DelayAsync(backoff, stoppingToken)) return;
                backoff = Min(backoff * 2, RetryBackoffMax);
            }

            // STEADY STATE: block until an order update arrives - nothing runs, and nothing is
            // fetched, between order events.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _resyncSignal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Let the rest of the basket's alerts land, then clear the flag BEFORE fetching:
                // an execution that happens mid-fetch must arm another round, not be swallowed by
                // the round that just missed it.
                if (!await DelayAsync(CoalesceWindow, stoppingToken)) break;
                Interlocked.Exchange(ref _orderUpdatePending, 0);

                // An order genuinely changed state, so the anchors are now known-stale - retry a
                // failed fetch until it succeeds rather than sitting on wrong quantities.
                backoff = RetryBackoffInitial;
                while (!stoppingToken.IsCancellationRequested && !await ResyncAsync(stoppingToken))
                {
                    if (!await DelayAsync(backoff, stoppingToken)) return;
                    backoff = Min(backoff * 2, RetryBackoffMax);
                }
            }
        }
        finally
        {
            _tickerClient.OrderUpdateReceived -= OnOrderUpdate;
            if (_brokerSwitchNotifier is not null) _brokerSwitchNotifier.Changed -= OnBrokerSwitched;
        }
    }

    /// <summary>Called on the ticker's receive loop - must stay non-blocking. The flag+semaphore
    /// pair means: first alert of a round releases the loop, every further alert until the loop
    /// clears the flag is coalesced into that same round.</summary>
    private void OnOrderUpdate()
    {
        if (Interlocked.CompareExchange(ref _orderUpdatePending, 1, 0) == 0)
        {
            // Says WHY the positions fetch that follows is happening. Without it those REST calls
            // appear in the log with no explanation at all - which reads like a stray poll, when in
            // fact this cache has no timer and only ever fetches in response to a broker event.
            _logger.LogInformation("Broker reported an order change - refreshing positions.");

            try { _resyncSignal.Release(); }
            catch (SemaphoreFullException) { /* already armed - coalesced */ }
        }
    }

    /// <summary>The anchors just went stale in a way ResyncAsync's normal failure handling must
    /// NOT paper over: they belong to a different broker account entirely, not merely an older
    /// snapshot from the same one. ResyncAsync keeps the previous anchors on a failed fetch
    /// deliberately (a stale-but-recent same-broker snapshot beats blanking the page), but that
    /// reasoning inverts across a platform switch - if the newly-active platform isn't
    /// authenticated yet, a failed resync must leave this EMPTY, never showing the outgoing
    /// platform's positions under the new one's name. Clearing synchronously here, before arming
    /// the resync via the exact same path an order update uses, guarantees that.</summary>
    private void OnBrokerSwitched(BrokerPlatform _)
    {
        lock (_lock) { _anchors = new Dictionary<string, PositionAnchor>(); }
        OnOrderUpdate();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Cancellable delay that reports completion instead of throwing, so the loops above
    /// can exit cleanly on shutdown.</summary>
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

    /// <summary>The manual-refresh path: fetches positions from the broker ONCE and, when the
    /// fetch succeeds, folds the snapshot into this cache's anchors exactly as the event-driven
    /// resync would - then returns the result for the caller to serve. This is what makes the
    /// Positions page's refresh button a true escape hatch under the no-timer design: with no
    /// periodic resync, a lost order-update event (socket mid-reconnect while a trade placed in
    /// the broker's own app fills) would otherwise leave the cache - and the PNL alerts and live
    /// SignalR push that read it - stale indefinitely, even while the page itself showed the
    /// operator fresh numbers. One REST call now serves both: the response the browser renders
    /// and the anchors everything else projects from. A failed fetch is returned as-is and leaves
    /// the existing anchors untouched.</summary>
    public async Task<PositionsResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var result = await _positionsService.GetPositionsAsync(cancellationToken);
        if (result.Success)
            await ApplyAsync(result, cancellationToken);
        return result;
    }

    /// <summary>Re-fetches the authoritative REST snapshot (quantity, average price, the broker's
    /// own pnl), rebuilds the anchor for every open position, and makes sure each one's
    /// instrument token is subscribed on the live ticker. Returns false on a failed fetch (no
    /// access token yet, transient API error), keeping whatever anchors already exist - a
    /// stale-but-recent snapshot is more useful than blanking everything out - so the caller's
    /// retry loop knows to try again.</summary>
    private async Task<bool> ResyncAsync(CancellationToken cancellationToken)
    {
        var result = await _positionsService.GetPositionsAsync(cancellationToken);
        if (!result.Success)
        {
            _logger.LogDebug("PositionsCache: REST resync failed ({Error}) - keeping previous snapshot, will retry.", result.ErrorMessage);
            return false;
        }

        await ApplyAsync(result, cancellationToken);
        return true;
    }

    /// <summary>Folds one successful positions fetch into the anchors and subscribes every
    /// resolved token - shared by the event-driven resync and the manual RefreshAsync so the two
    /// can never drift. Concurrent callers are benign: the anchor dictionary is swapped whole
    /// under the lock (last completed fetch wins, and both are equally fresh), and ticker
    /// subscription is idempotent.</summary>
    private async Task ApplyAsync(PositionsResult result, CancellationToken cancellationToken)
    {
        var newAnchors = new Dictionary<string, PositionAnchor>();
        var tokensToSubscribe = new List<uint>();

        foreach (var position in result.Positions)
        {
            // The positions service already resolved this - on Dhan from the securityId on the
            // position row itself, on Kite from the instrument-master lookup it does for lot size.
            // Trust that over re-deriving it here: a symbol lookup silently returns 0 whenever the
            // broker's spelling differs from the instrument master's, and a 0 token means this
            // position never gets subscribed, so it would show no live PNL or exit price at all.
            // The lookup is kept only as a fallback for a snapshot that couldn't resolve one, and
            // is a plain in-memory hit either way (the master is already loaded by this point).
            var token = position.InstrumentToken != 0
                ? position.InstrumentToken
                : _instrumentMaster.Find(position.TradingSymbol, position.Exchange)?.InstrumentToken ?? 0;

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
