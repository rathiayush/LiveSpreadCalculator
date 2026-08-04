using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Resolves every configured spread leg to a Kite instrument_token, then owns the single Kite
/// ticker WebSocket connection for the life of the process, writing every tick into the shared
/// ITickStore. Also exposes new tokens to the already-running ticker whenever a spread is added
/// later through the API (see ResolveAndSubscribeAsync, called from SpreadsController).
///
/// Also runs a periodic reconciliation loop (ReconcileSubscriptionsLoopAsync) that unsubscribes
/// and drops the tick-store entry for any instrument token nothing needs any more - neither a
/// currently-configured (enabled) spread leg nor a currently-open position (see
/// PositionsCache.GetAnchoredTokens). Before this existed, SubscribeAsync had no unsubscribe
/// counterpart at all: every token this process ever subscribed to, across every deleted spread
/// and every closed position over the life of the process, stayed subscribed on the live ticker
/// and stayed in ITickStore forever - a slow, unbounded leak for a singleton process meant to run
/// for weeks/months without a restart (see ARCHITECTURE.md §8).
/// </summary>
public sealed class TickIngestionHostedService : BackgroundService
{
    // Deliberately not aggressive - reconciliation only ever removes something a fresh recompute
    // confirms is unneeded right now, so a slower cadence just means stale tokens linger a bit
    // longer, not that anything is ever removed while still genuinely in use. If a token
    // reconciliation drops turns out to be needed again moments later (e.g. a spread recreated
    // with the same legs), SubscribeAsync just re-adds it - self-healing either way.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly IMarketDataFeed _tickerClient;
    private readonly ITickStore _tickStore;
    private readonly PositionsCache _positionsCache;
    private readonly ILogger<TickIngestionHostedService> _logger;

    public TickIngestionHostedService(
        ISpreadDefinitionStore spreadStore,
        IBrokerInstrumentMaster instrumentMaster,
        IMarketDataFeed tickerClient,
        ITickStore tickStore,
        PositionsCache positionsCache,
        ILogger<TickIngestionHostedService> logger)
    {
        _spreadStore = spreadStore;
        _instrumentMaster = instrumentMaster;
        _tickerClient = tickerClient;
        _tickStore = tickStore;
        _positionsCache = positionsCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tickerClient.TickReceived += _tickStore.Update;
        try
        {
            await _instrumentMaster.EnsureLoadedAsync(stoppingToken);

            var tokens = new HashSet<uint>();
            foreach (var spread in _spreadStore.GetAll().Where(s => s.Enabled))
            {
                await ResolveLegAsync(spread.NearLeg, stoppingToken);
                await ResolveLegAsync(spread.FarLeg, stoppingToken);
                ResolveSpotLeg(spread);
                if (spread.NearLeg.InstrumentToken != 0) tokens.Add(spread.NearLeg.InstrumentToken);
                if (spread.FarLeg.InstrumentToken != 0) tokens.Add(spread.FarLeg.InstrumentToken);
                if (spread.SpotLeg is { InstrumentToken: not 0 } spot) tokens.Add(spot.InstrumentToken);
            }

            if (tokens.Count == 0)
            {
                _logger.LogWarning(
                    "No spreads configured yet - the ticker will connect but subscribe to nothing. " +
                    "Configure spreads via POST /api/spreads and it will subscribe automatically.");
            }

            // Runs alongside RunAsync (which blocks for the process lifetime, reconnecting on
            // failure) rather than after it - there is no "after" until shutdown.
            await Task.WhenAll(
                _tickerClient.RunAsync(tokens, stoppingToken),
                ReconcileSubscriptionsLoopAsync(stoppingToken));
        }
        finally
        {
            _tickerClient.TickReceived -= _tickStore.Update;
        }
    }

    private async Task ReconcileSubscriptionsLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tick subscription reconciliation failed - will retry next cycle.");
            }
        }
    }

    /// <summary>Recomputes "what does this process actually need live ticks for" from scratch
    /// (every enabled spread's near/far leg, plus every currently-open position - see
    /// PositionsCache.GetAnchoredTokens) rather than tracking deltas per call site (spread
    /// deleted, position closed, ...), so a missed call site anywhere can never leave a stale
    /// subscription behind - the next cycle always catches it regardless of what caused it.
    /// Whatever the live ticker is still subscribed to beyond that recomputed set gets
    /// unsubscribed and dropped from ITickStore.</summary>
    private async Task ReconcileSubscriptionsAsync(CancellationToken stoppingToken)
    {
        var needed = new HashSet<uint>();
        foreach (var spread in _spreadStore.GetAll().Where(s => s.Enabled))
        {
            if (spread.NearLeg.InstrumentToken != 0) needed.Add(spread.NearLeg.InstrumentToken);
            if (spread.FarLeg.InstrumentToken != 0) needed.Add(spread.FarLeg.InstrumentToken);
            // Spot counts as needed too - it is what the basis % is computed against. Leaving it
            // out here would have this loop unsubscribe every spot token within five minutes of
            // startup as "used by nothing", silently emptying the spot column again.
            if (spread.SpotLeg is { InstrumentToken: not 0 } spot) needed.Add(spot.InstrumentToken);
        }
        foreach (var token in _positionsCache.GetAnchoredTokens()) needed.Add(token);

        var stale = _tickerClient.GetSubscribedTokens().Where(t => !needed.Contains(t)).ToArray();
        if (stale.Length == 0) return;

        await _tickerClient.UnsubscribeAsync(stale, stoppingToken);
        foreach (var token in stale) _tickStore.Remove(token);

        _logger.LogInformation(
            "Unsubscribed {Count} instrument token(s) no longer used by any configured spread or open position.",
            stale.Length);
    }

    /// <summary>Resolves a spread's legs to instrument tokens and subscribes the running
    /// ticker to them immediately. Called by SpreadsController when a spread is added at
    /// runtime, so the dashboard doesn't require a restart to start streaming a new pair.</summary>
    public async Task ResolveAndSubscribeAsync(SpreadDefinition spread, CancellationToken cancellationToken)
    {
        await ResolveLegAsync(spread.NearLeg, cancellationToken);
        await ResolveLegAsync(spread.FarLeg, cancellationToken);
        ResolveSpotLeg(spread);

        var tokens = new List<uint>();
        if (spread.NearLeg.InstrumentToken != 0) tokens.Add(spread.NearLeg.InstrumentToken);
        if (spread.FarLeg.InstrumentToken != 0) tokens.Add(spread.FarLeg.InstrumentToken);
        if (spread.SpotLeg is { InstrumentToken: not 0 } spot) tokens.Add(spot.InstrumentToken);

        if (tokens.Count > 0)
            await _tickerClient.SubscribeAsync(tokens, cancellationToken);
    }

    /// <summary>
    /// Attaches the underlying's cash/index instrument to a spread so its spot price can stream -
    /// see SpreadDefinition.SpotLeg. The underlying name is taken from the near leg once it has
    /// resolved (the instrument master fills it in), falling back to the spread's own Underlying.
    ///
    /// Best-effort and non-fatal by design: an underlying with no matching spot instrument just
    /// leaves SpotLeg null and shows no spot, which is the safe way to fail - see SpotSymbology.
    /// Logged at Debug rather than Warning because on a full universe the misses are the handful of
    /// contracts with genuinely no cash equivalent, and a per-spread warning would be pure noise.
    /// </summary>
    /// <summary>
    /// Public because UniverseBuilderService has to call it too. It builds spreads in bulk and
    /// subscribes their tokens in one batch rather than going through ResolveAndSubscribeAsync
    /// above, so without calling this the ENTIRE auto-built universe - which is how essentially
    /// every spread is created - got no spot leg and no spot subscription, and every Spot/Basis
    /// cell on the grid stayed blank. Only per-spread additions made through the API worked.
    /// </summary>
    /// <returns>The resolved spot instrument token, or 0 if this underlying has no spot - which
    /// callers should treat as "nothing to subscribe", not as a failure.</returns>
    public uint ResolveSpotLeg(SpreadDefinition spread)
    {
        if (spread.SpotLeg is { InstrumentToken: not 0 } existing) return existing.InstrumentToken;

        var underlying = spread.NearLeg.Underlying ?? spread.Underlying;
        if (string.IsNullOrWhiteSpace(underlying)) return 0;

        try
        {
            var spot = _instrumentMaster.FindSpot(underlying);
            if (spot is null)
            {
                _logger.LogDebug("No spot instrument found for underlying {Underlying} - {SpreadId} will show no spot price.", underlying, spread.Id);
                return 0;
            }

            spread.SpotLeg = new InstrumentConfig
            {
                TradingSymbol = spot.TradingSymbol,
                Exchange = spot.Exchange,
                InstrumentToken = spot.InstrumentToken,
                Underlying = underlying,
                TickSize = spot.TickSize,
            };

            return spot.InstrumentToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve a spot instrument for underlying {Underlying}", underlying);
            return 0;
        }
    }

    /// <summary>Subscribes the running ticker to a batch of already-resolved instrument tokens
    /// in a single subscribe + mode WebSocket round trip, instead of one round trip per token
    /// pair. Used by UniverseBuilderService when building tens/hundreds of spreads at once -
    /// calling ResolveAndSubscribeAsync per-spread there was firing hundreds of separate
    /// subscribe/mode messages back-to-back, which is what was causing some instruments to
    /// silently end up without depth (never got mode=full applied) even though the exchange
    /// itself had live depth for them.</summary>
    public Task SubscribeTokensAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken) =>
        _tickerClient.SubscribeAsync(instrumentTokens, cancellationToken);

    private async Task ResolveLegAsync(InstrumentConfig config, CancellationToken cancellationToken)
    {
        if (config.InstrumentToken != 0) return;
        try
        {
            await _instrumentMaster.ResolveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve instrument token for {Symbol}", config.TradingSymbol);
        }
    }
}
