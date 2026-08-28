using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Store;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Delta-weighted Cumulative Delta Volume (CDV) for an index option chain's CE/PE order flow -
/// a live port of the "delta auto with Delta-Weighted CDV" Pine Script indicator, Dhan-only (see
/// CdvOptions.Underlyings/DhanInstrumentMasterService's option-chain parsing).
///
/// WHAT CHANGED FROM THE PINE SCRIPT, AND WHY:
///
///  - BUY/SELL SPLIT: Pine has no real tick data, so it proxies per-bar delta from where the CLOSE
///    sits inside that bar's HIGH/LOW range. This app has real ticks (see Tick.cs), so each trade
///    is classified buy-/sell-initiated by comparing its LastPrice to that instant's best bid/ask
///    (the standard tick rule) instead - a materially better order-flow signal, not just a port -
///    see SignedDelta.
///
///  - STRIKE RESOLUTION: Pine hand-builds an expiry code and 32 option tickers from scratch every
///    bar. Here the ATM±N strike chain is resolved once, then both the ATM strike AND the nearest
///    listed expiry are re-checked every CdvOptions.AtmRecenterIntervalMinutes (default 30) against
///    Dhan's own scrip master via DhanInstrumentMasterService.FindOption/GetNearestOptionExpiry -
///    the real contract list, always the nearest expiry, never a maintained date table that goes
///    stale, and never stuck on an expiry that has since rolled to the next week.
///
///  - LIVE VALUES COME ONLY FROM THE EXISTING WEBSOCKET FEED (IMarketDataFeed.TickReceived, the
///    same live "hook" TickIngestionHostedService already uses for every other instrument this app
///    streams) - never REST-polled. The only REST traffic this feature adds at all is the scrip
///    master's own existing once-a-day download (InstrumentMasterRefreshHostedService), which
///    already runs regardless of CDV.
///
///  - "LOOKBACK N BARS" / EMA / % CHANGE stay bar-indexed, exactly like the Pine script - see
///    CdvOptions.TimeframeSeconds. Each strike's own running CDV is sampled onto a fixed-cadence
///    synthetic bar series (StrikeRuntime.BarValues), index-aligned across every strike for a given
///    underlying, so "bar 37" always means the same instant for every strike. The published
///    CE/PE totals are just an elementwise sum of the currently-displayed window's strikes at each
///    bar index.
///
///  - ATM RE-CENTRING: Pine's ATM is a fixed manual input. This app has a live spot price instead,
///    so ATM is recomputed from it every CdvOptions.AtmRecenterIntervalMinutes. Critically, the
///    chain is always SUBSCRIBED wider (CdvOptions.SubscribeStrikeRange) than it is DISPLAYED
///    (CdvOptions.DisplayStrikeRange), so a re-centre almost never needs a fresh subscription -
///    every strike that could plausibly enter the displayed window already has a full same-day bar
///    history. On a re-centre the published series is not reset; it is RECOMPUTED from that
///    history - EMA replayed from bar 0 over the newly-selected strikes' sums - so the chart never
///    shows a discontinuity or loses today's data just because the window shifted.
/// </summary>
public sealed class CdvHostedService : BackgroundService
{
    private static readonly TimeZoneInfo Ist = ResolveIst();

    /// <summary>Pine's per-index strikeStep switch, narrowed to this feature's two supported
    /// underlyings - see CdvOptions.Underlyings.</summary>
    private static readonly Dictionary<string, int> StrikeSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = 50,
        ["BANKNIFTY"] = 100,
    };

    private static readonly bool[] OptionRights = { true, false }; // CE, PE

    private readonly IOptionChainSource _instrumentMaster;
    private readonly IMarketDataFeed _marketDataFeed;
    private readonly ITickStore _tickStore;
    private readonly ICdvPublisher _publisher;
    private readonly CdvTokenRegistry _tokenRegistry;
    private readonly IInstrumentSegmentRegistry _segmentRegistry;
    private readonly ISpotPriceBootstrap _spotBootstrap;
    private readonly IHistoricalCandleSource _historicalCandles;
    private readonly CdvOptions _options;
    private readonly ILogger<CdvHostedService> _logger;

    // Only ever mutated from the single ExecuteAsync/PeriodicTimer loop (never reentrant), so a
    // plain Dictionary is safe.
    private readonly Dictionary<string, UnderlyingState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedUnderlyings = new(StringComparer.OrdinalIgnoreCase);

    // Tracks the last trading day the one-shot REST bootstrap (see ISpotPriceBootstrap) was
    // attempted for each underlying, so it fires at most once per underlying per day regardless of
    // outcome - a stale/mismatched entry here (including "never attempted") is what triggers the
    // next attempt, so this self-resets on a new day with no explicit clearing needed.
    private readonly Dictionary<string, DateOnly> _bootstrapAttemptedDay = new(StringComparer.OrdinalIgnoreCase);

    // Written from the ExecuteAsync loop (EnsureChainAsync) but READ from OnTick, which runs on the
    // feed's own receive-loop thread (see IMarketDataFeed.TickReceived's doc comment) - genuinely
    // concurrent with the timer loop, so this has to be thread-safe, unlike the two fields above.
    private readonly ConcurrentDictionary<uint, TokenTarget> _tokenIndex = new();

    // Logged-once guard for the segment-mismatch warning in OnTick, also read/written from the
    // feed's receive-loop thread - a plain HashSet isn't safe there, so this reuses the same
    // ConcurrentDictionary-as-a-set trick as _tokenIndex.
    private readonly ConcurrentDictionary<uint, byte> _warnedCollisions = new();

    public CdvHostedService(
        IOptionChainSource instrumentMaster,
        IMarketDataFeed marketDataFeed,
        ITickStore tickStore,
        ICdvPublisher publisher,
        CdvTokenRegistry tokenRegistry,
        IInstrumentSegmentRegistry segmentRegistry,
        ISpotPriceBootstrap spotBootstrap,
        IHistoricalCandleSource historicalCandles,
        IOptions<CdvOptions> options,
        ILogger<CdvHostedService> logger)
    {
        _instrumentMaster = instrumentMaster;
        _marketDataFeed = marketDataFeed;
        _tickStore = tickStore;
        _publisher = publisher;
        _tokenRegistry = tokenRegistry;
        _segmentRegistry = segmentRegistry;
        _spotBootstrap = spotBootstrap;
        _historicalCandles = historicalCandles;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("CDV feed disabled (Cdv:Enabled=false) - not starting.");
            return;
        }

        // Explicit, unconditional confirmation that this service is actually running and what it
        // thinks it's configured to do - deliberately Information (not just the failure-path
        // warnings below), so "is CdvHostedService even executing" is answerable from a single grep
        // instead of inferring it from the absence of later log lines. An empty Underlyings list
        // here (misconfigured Cdv:Underlyings, or a config-binding problem) is the single most
        // likely reason EnsureChainAsync would never be called at all - this line makes that
        // impossible to miss.
        _logger.LogInformation(
            "CDV feed starting: underlyings=[{Underlyings}], bar size={TimeframeSeconds}s, " +
            "re-centre every={AtmRecenterIntervalMinutes}min, smoothLen={SmoothLen}, lookbackBars={LookbackBars}.",
            string.Join(", ", _options.Underlyings), _options.TimeframeSeconds,
            _options.AtmRecenterIntervalMinutes, _options.SmoothLen, _options.LookbackBars);

        if (_options.Underlyings.Count == 0)
        {
            _logger.LogWarning("Cdv:Underlyings is empty - CDV will run its timer loop but never resolve or publish anything for any underlying.");
        }

        if (_options.TimeframeSeconds < 5)
        {
            _logger.LogWarning("Cdv:TimeframeSeconds ({Value}) is too low - clamping to 5s.", _options.TimeframeSeconds);
        }

        _marketDataFeed.TickReceived += OnTick;
        try
        {
            try
            {
                await _instrumentMaster.EnsureLoadedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An unhandled exception here would otherwise propagate out of ExecuteAsync - and a
                // BackgroundService's default BackgroundServiceExceptionBehavior is Ignore, so the
                // host would silently let this entire hosted service die with NOTHING further ever
                // logged again, forever, rather than crashing loudly. Log it explicitly and give up
                // for this run rather than let that happen invisibly.
                _logger.LogError(ex, "CDV feed could not load the instrument master and will not start this run.");
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.TimeframeSeconds)));
            do
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CDV cycle failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            _marketDataFeed.TickReceived -= OnTick;
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var todayIst = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Ist).Date);

        foreach (var underlying in _options.Underlyings)
        {
            if (!StrikeSteps.TryGetValue(underlying, out var strikeStep))
            {
                if (_warnedUnderlyings.Add(underlying))
                    _logger.LogWarning("Cdv:Underlyings contains '{Underlying}' but it has no known strike step - skipping.", underlying);
                continue;
            }

            var state = _states.TryGetValue(underlying, out var existing) ? existing : null;

            var isNewDay = state is null || state.TradingDay != todayIst;
            var dueToRecenter = isNewDay || state is null ||
                nowUtc - state.LastAtmRecenterUtc >= TimeSpan.FromMinutes(Math.Max(1, _options.AtmRecenterIntervalMinutes));

            if (isNewDay || dueToRecenter)
            {
                state = await EnsureChainAsync(state, underlying, strikeStep, todayIst, isNewDay, nowUtc, stoppingToken);
                if (state is null) continue; // couldn't resolve yet (no spot tick, no expiry, etc.) - retry next cycle
                _states[underlying] = state;
            }

            if (state is null) continue;

            AppendBarAndPublish(state, nowUtc);
        }

        _tokenRegistry.SetTokens(_tokenIndex.Keys.ToArray());
    }

    /// <summary>Resolves (or re-centres) one underlying's strike chain. Returns null when it can't
    /// be resolved yet (no spot instrument, no listed expiry, or spot hasn't ticked) - the caller
    /// just retries next cycle, same "skip until the data exists" pattern used throughout this
    /// codebase (see SpreadCalculationHostedService.RunCycleAsync).</summary>
    private async Task<UnderlyingState?> EnsureChainAsync(
        UnderlyingState? state, string underlying, int strikeStep, DateOnly todayIst, bool isNewDay,
        DateTimeOffset nowUtc, CancellationToken stoppingToken)
    {
        await _instrumentMaster.EnsureLoadedAsync(stoppingToken);

        var spot = _instrumentMaster.FindSpot(underlying);
        if (spot is null)
        {
            _logger.LogWarning("CDV: no spot instrument resolved for {Underlying} - skipping.", underlying);
            return null;
        }
        _segmentRegistry.Remember(spot.InstrumentToken, spot.Segment);

        var expiry = _instrumentMaster.GetNearestOptionExpiry(underlying, todayIst);
        if (expiry is null)
        {
            _logger.LogWarning("CDV: no listed option expiry found for {Underlying} - skipping.", underlying);
            return null;
        }

        // Same collision risk as OnTick (see Tick.Segment's doc comment) - the shared ITickStore is
        // keyed by bare token too, so a tick for a completely different Dhan instrument that happens
        // to share this token's numeric id in another segment could otherwise be read as this
        // underlying's spot price.
        var validSpotTick = _tickStore.TryGet(spot.InstrumentToken, out var spotTick) &&
            (spotTick.Segment is null || string.Equals(spotTick.Segment, spot.Exchange, StringComparison.OrdinalIgnoreCase));

        decimal spotPrice;
        if (validSpotTick && SpotPrice(spotTick) > 0)
        {
            spotPrice = SpotPrice(spotTick);
        }
        else
        {
            // Spot hasn't ticked yet this process lifetime - subscribe it now (harmless if already
            // subscribed). This used to just return and wait for next cycle's live tick; now it
            // also tries a ONE-TIME REST bootstrap (see ISpotPriceBootstrap) so ATM resolution isn't
            // blocked for however long the live feed takes to start streaming this instrument -
            // every price after this single call still comes from the live feed exactly as before.
            _logger.LogWarning(
                "CDV: {Underlying} spot (token {Token}) has not ticked yet - (re)subscribing.",
                underlying, spot.InstrumentToken);
            await _marketDataFeed.SubscribeAsync(new[] { spot.InstrumentToken }, stoppingToken);

            decimal? bootstrapPrice = null;
            if (!_bootstrapAttemptedDay.TryGetValue(underlying, out var attemptedDay) || attemptedDay != todayIst)
            {
                // Marked BEFORE the call, and regardless of outcome - a failed attempt (not
                // authenticated, network error) must not retry every single cycle for the rest of
                // the day; the live feed is still the fallback either way.
                _bootstrapAttemptedDay[underlying] = todayIst;
                bootstrapPrice = await _spotBootstrap.GetLastTradedPriceAsync(spot.InstrumentToken, spot.Segment, stoppingToken);
                if (bootstrapPrice is > 0)
                {
                    _logger.LogInformation(
                        "CDV: bootstrapped {Underlying} spot via REST LTP ({Price}) while waiting for the live feed to start ticking.",
                        underlying, bootstrapPrice);
                }
            }

            if (bootstrapPrice is not > 0) return state; // still nothing to work with - retry next cycle
            spotPrice = bootstrapPrice.Value;
        }
        var atm = (int)(Math.Round(spotPrice / strikeStep, MidpointRounding.AwayFromZero) * strikeStep);

        var freshDay = isNewDay || state is null || state.Expiry != expiry.Value;
        if (freshDay)
        {
            // New trading day (or the nearest expiry just rolled) - the whole per-strike history is
            // no longer meaningful (yesterday's flow, or a different contract's), so start clean.
            foreach (var token in state?.Strikes.Values.Select(s => s.Token) ?? Enumerable.Empty<uint>())
                _tokenIndex.TryRemove(token, out _);
            if (state is not null) _tokenIndex.TryRemove(state.SpotToken, out _);

            state = new UnderlyingState(underlying, strikeStep, expiry.Value, todayIst);
        }

        // freshDay is false here only when state was already non-null going in (freshDay's own
        // definition includes "state is null"), so this is always non-null - the compiler just
        // can't follow that cross-variable proof through the `if` above.
        state!.Atm = atm;
        state.SpotToken = spot.InstrumentToken;
        lock (state) state.LastSpot = spotPrice; // OnTick's spot-tick branch also writes this under lock(state)
        state.LastAtmRecenterUtc = nowUtc;
        _tokenIndex[spot.InstrumentToken] = new TokenTarget(state, null, spot.Exchange);

        var toSubscribe = new List<uint> { spot.InstrumentToken };
        var barCount = state.Strikes.Values.Select(s => s.BarValues.Count).DefaultIfEmpty(0).Max();

        for (var i = -_options.SubscribeStrikeRange; i <= _options.SubscribeStrikeRange; i++)
        {
            var strike = atm + i * strikeStep;
            if (strike <= 0) continue;

            foreach (var isCall in OptionRights)
            {
                var key = StrikeKey(strike, isCall);
                if (state.Strikes.ContainsKey(key)) continue; // already tracked from an earlier cycle

                var contract = _instrumentMaster.FindOption(underlying, expiry.Value, strike, isCall);
                if (contract is null) continue; // strike not listed for this expiry - skip, like Pine's nz()

                // CRITICAL: an option contract is never in IBrokerInstrumentMaster's futures-only
                // lookups, which is all DhanMarketFeedClient's segment lookup otherwise consults -
                // without this, SubscribeAsync below would silently drop every one of these tokens
                // (logged only as "no entry in the Dhan scrip master, so their exchange segment is
                // unknown") and no option tick would ever arrive. See IInstrumentSegmentRegistry.
                _segmentRegistry.Remember(contract.InstrumentToken, contract.Segment);

                var runtime = new StrikeRuntime(contract.InstrumentToken, strike, isCall);
                // Backfill zeros so every strike's BarValues stays index-aligned with the rest of
                // today's series, whether it was subscribed at day open or added by a later
                // re-centre - see the class doc comment on recomputing (not resetting) the chart.
                for (var b = 0; b < barCount; b++) runtime.BarValues.Add(0m);

                state.Strikes[key] = runtime;
                _tokenIndex[contract.InstrumentToken] = new TokenTarget(state, runtime, contract.Exchange);
                toSubscribe.Add(contract.InstrumentToken);
            }
        }

        if (toSubscribe.Count > 0)
            await _marketDataFeed.SubscribeAsync(toSubscribe, stoppingToken);

        if (freshDay && !state.PreviousDayBackfillAttempted)
        {
            state.PreviousDayBackfillAttempted = true; // once per day regardless of outcome - never retried mid-day
            await BackfillPreviousDayAsync(state, underlying, todayIst, stoppingToken);
        }

        return state;
    }

    /// <summary>Reconstructs the previous trading day's CE/PE CDV curve from Dhan's historical
    /// intraday candles, using the SAME contracts (strike + this same near expiry) CDV is about to
    /// track today - a deliberate simplification over re-resolving yesterday's own ATM/expiry from
    /// scratch, since any contract with today's expiry (the nearest one >= today) was necessarily
    /// already listed and tradeable yesterday too. Only covers the DISPLAY window (±DisplayStrikeRange),
    /// not the wider subscribe range - matches what's actually shown, and keeps the number of REST
    /// calls (one per contract, no batching on this Dhan endpoint) bounded.
    ///
    /// Historical candles carry no real trade-by-trade buy/sell tagging, so this falls back to the
    /// Pine Script's OWN bar-proxy formula (close-in-range) for delta classification - the same
    /// approximation Pine itself is built on, applied here only to a day that has already closed,
    /// never to live data (see SignedDelta for the real tick-rule classification live ticks use).
    /// Delta-weighting uses TODAY's spot/ATM as a fixed reference for the whole backfilled day, not a
    /// per-minute historical spot series (not available) - another accepted approximation.</summary>
    private async Task BackfillPreviousDayAsync(UnderlyingState state, string underlying, DateOnly todayIst, CancellationToken cancellationToken)
    {
        var previousDay = PreviousWeekday(todayIst);
        var intervalMinutes = Math.Max(1, _options.TimeframeSeconds / 60);

        var windowLo = state.Atm - _options.DisplayStrikeRange * state.StrikeStep;
        var windowHi = state.Atm + _options.DisplayStrikeRange * state.StrikeStep;
        var strikesToBackfill = state.Strikes.Values
            .Where(s => s.Strike >= windowLo && s.Strike <= windowHi)
            .ToArray();

        if (strikesToBackfill.Length == 0) return;

        decimal[]? ceSeries = null;
        decimal[]? peSeries = null;
        var fetchedCount = 0;

        foreach (var s in strikesToBackfill)
        {
            IReadOnlyList<HistoricalCandle> candles;
            try
            {
                // CDV only ever deals in NFO index options (see CdvOptions.Underlyings) - always the
                // same Dhan segment, so this is a constant rather than something read off the
                // resolved contract (StrikeRuntime doesn't carry it - only the token is needed for
                // the live feed, which resolves segment via IInstrumentSegmentRegistry instead).
                candles = await _historicalCandles.GetIntradayCandlesAsync(s.Token, "NSE_FNO", previousDay, intervalMinutes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "CDV backfill: request threw for {Underlying} strike {Strike}{Right} on {Day}", underlying, s.Strike, s.IsCall ? "CE" : "PE", previousDay);
                continue;
            }

            if (candles.Count == 0) continue;
            fetchedCount++;

            var weight = _options.UseDeltaWeighting ? (decimal)DeltaWeight(s.Strike, s.IsCall, state.LastSpot) : 1m;
            var series = s.IsCall ? (ceSeries ??= new decimal[candles.Count]) : (peSeries ??= new decimal[candles.Count]);
            if (series.Length < candles.Count)
            {
                // A different contract returned more candles than the first one did (shouldn't
                // normally happen - same session, same day) - grow rather than drop data.
                var grown = new decimal[candles.Count];
                Array.Copy(series, grown, series.Length);
                series = grown;
                if (s.IsCall) ceSeries = grown; else peSeries = grown;
            }

            var running = 0m;
            for (var i = 0; i < candles.Count; i++)
            {
                running += BarProxyDelta(candles[i]) * weight;
                series[i] += running;
            }
        }

        if (ceSeries is not null) state.PreviousDayCe.AddRange(ceSeries);
        if (peSeries is not null) state.PreviousDayPe.AddRange(peSeries);

        _logger.LogInformation(
            "CDV: backfilled {Underlying} previous day ({Day}) from {Fetched}/{Total} contracts - {CeBars} CE bars, {PeBars} PE bars.",
            underlying, previousDay, fetchedCount, strikesToBackfill.Length, state.PreviousDayCe.Count, state.PreviousDayPe.Count);
    }

    /// <summary>Pine's f_bar_delta_with_delta/f_bar_delta_standard, applied to a historical candle
    /// instead of a live bar - the only buy/sell classification possible from OHLCV data alone, no
    /// different from what Pine itself is limited to.</summary>
    private static decimal BarProxyDelta(HistoricalCandle candle)
    {
        var hl = candle.High - candle.Low;
        if (hl <= 0.0001m) return 0m;

        var buyVol = (candle.Close - candle.Low) / hl * candle.Volume;
        var sellVol = (candle.High - candle.Close) / hl * candle.Volume;
        return buyVol - sellVol;
    }

    private static DateOnly PreviousWeekday(DateOnly day)
    {
        var previous = day.AddDays(-1);
        while (previous.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            previous = previous.AddDays(-1);
        return previous;
    }

    private static decimal SpotPrice(Tick tick) =>
        tick.LastPrice > 0 ? tick.LastPrice : tick.BestAskPrice > 0 ? tick.BestAskPrice : tick.BestBidPrice;

    private void OnTick(Tick tick)
    {
        if (!_tokenIndex.TryGetValue(tick.InstrumentToken, out var target)) return;

        // CRITICAL: Dhan's securityId is only unique WITHIN a segment (confirmed against a real
        // scrip master download: securityId 13 is simultaneously ABB India's equity AND the NIFTY
        // 50 index, in different segments) - InstrumentToken alone is not enough to know this tick
        // actually belongs to the instrument this token was registered for. tick.Segment is null on
        // feeds that don't need this distinction (Kite's token IS globally unique) - only reject
        // when it's actually populated and disagrees.
        if (tick.Segment is not null && !string.Equals(tick.Segment, target.ExpectedExchange, StringComparison.OrdinalIgnoreCase))
        {
            if (_warnedCollisions.TryAdd(tick.InstrumentToken, 0))
            {
                _logger.LogWarning(
                    "CDV: ignoring a tick for token {Token} whose segment ({ActualSegment}) doesn't match what CDV resolved it as ({ExpectedSegment}) - " +
                    "this token means a different instrument in another Dhan segment.",
                    tick.InstrumentToken, tick.Segment, target.ExpectedExchange);
            }
            return;
        }

        if (target.Strike is not { } strike)
        {
            // Spot tick - just keep the last known price fresh for delta-weighting; ATM itself is
            // only recomputed on the fixed re-centre cadence (see EnsureChainAsync).
            lock (target.State) target.State.LastSpot = SpotPrice(tick);
            return;
        }

        var delta = SignedDelta(tick);
        if (delta == 0m) return;

        var weight = _options.UseDeltaWeighting
            ? (decimal)DeltaWeight(strike.Strike, strike.IsCall, target.State.LastSpot)
            : 1m;

        lock (strike) strike.RunningCdv += delta * weight;
    }

    /// <summary>Classifies one trade as buy- or sell-initiated by comparing its price to the best
    /// bid/ask at that instant (the standard "tick rule") and returns its signed traded quantity -
    /// positive for a buy, negative for a sell, 0 if depth hasn't arrived yet (skipped, same as
    /// every other depth-dependent calculation in this app) or the tick carries no new trade.</summary>
    private static decimal SignedDelta(Tick tick)
    {
        if (tick.LastTradedQuantity <= 0 || tick.BestBidPrice <= 0 || tick.BestAskPrice <= 0) return 0m;

        if (tick.LastPrice >= tick.BestAskPrice) return tick.LastTradedQuantity;
        if (tick.LastPrice <= tick.BestBidPrice) return -tick.LastTradedQuantity;

        var mid = (tick.BestBidPrice + tick.BestAskPrice) / 2m;
        return tick.LastPrice > mid ? tick.LastTradedQuantity
             : tick.LastPrice < mid ? -tick.LastTradedQuantity
             : 0m;
    }

    /// <summary>Pine's f_get_delta, unchanged: a sigmoid-of-moneyness proxy for the option's own
    /// delta, used only as a WEIGHT (its absolute value) so far-OTM strikes contribute less to the
    /// CE/PE total than near-ATM ones - not a real Black-Scholes greek.</summary>
    private static double DeltaWeight(decimal strike, bool isCall, decimal spot)
    {
        if (spot <= 0) return 1.0;
        var moneyness = (double)((strike - spot) / spot);
        var raw = isCall
            ? 1.0 / (1.0 + Math.Exp(-1.7 * -moneyness * 5.0))
            : -1.0 / (1.0 + Math.Exp(-1.7 * moneyness * 5.0));
        return Math.Abs(raw);
    }

    /// <summary>Samples every tracked strike's running CDV onto this bar, rebuilds the displayed
    /// CE/PE series (an elementwise sum over the current display window - correct whether or not
    /// this cycle also re-centred ATM), recomputes the EMA from bar 0, and publishes.</summary>
    private void AppendBarAndPublish(UnderlyingState state, DateTimeOffset nowUtc)
    {
        lock (state)
        {
            foreach (var s in state.Strikes.Values)
                lock (s) s.BarValues.Add(s.RunningCdv);

            var windowLo = state.Atm - _options.DisplayStrikeRange * state.StrikeStep;
            var windowHi = state.Atm + _options.DisplayStrikeRange * state.StrikeStep;
            var ceStrikes = state.Strikes.Values.Where(s => s.IsCall && s.Strike >= windowLo && s.Strike <= windowHi).ToArray();
            var peStrikes = state.Strikes.Values.Where(s => !s.IsCall && s.Strike >= windowLo && s.Strike <= windowHi).ToArray();

            var barCount = state.Strikes.Values.Select(s => s.BarValues.Count).DefaultIfEmpty(0).Max();
            if (barCount == 0) return; // no strike resolved yet for this underlying - nothing to publish

            var ceSeries = new decimal[barCount];
            var peSeries = new decimal[barCount];
            foreach (var s in ceStrikes)
                for (var i = 0; i < s.BarValues.Count; i++) ceSeries[i] += s.BarValues[i];
            foreach (var s in peStrikes)
                for (var i = 0; i < s.BarValues.Count; i++) peSeries[i] += s.BarValues[i];

            var (ceEmaSeries, peEmaSeries) = ComputeEma(ceSeries, peSeries, _options.SmoothLen);

            var lastIndex = barCount - 1;
            var ceEma = ceEmaSeries[lastIndex];
            var peEma = peEmaSeries[lastIndex];

            var prevCeEma = state.PrevCeEma;
            var prevPeEma = state.PrevPeEma;
            var crossUp = prevCeEma is { } pc1 && prevPeEma is { } pp1 && pc1 <= pp1 && ceEma > peEma;
            var crossDown = prevCeEma is { } pc2 && prevPeEma is { } pp2 && pc2 >= pp2 && ceEma < peEma;
            state.PrevCeEma = ceEma;
            state.PrevPeEma = peEma;

            var (ceLbPct, peLbPct) = LookbackChange(ceEmaSeries, peEmaSeries, _options.LookbackBars);
            var ceLbHit = ceLbPct is { } clp && Math.Abs(clp) >= _options.LookbackPctThreshold;
            var peLbHit = peLbPct is { } plp && Math.Abs(plp) >= _options.LookbackPctThreshold;

            if (!state.FrozenCaptured && IsFrozenTime(nowUtc, _options.FrozenTimeIst, _options.TimeframeSeconds))
            {
                state.CeFrozenValue = ceEma;
                state.PeFrozenValue = peEma;
                state.FrozenCaptured = true;
            }

            decimal? ceFrozenPct = state.CeFrozenValue is { } cfv && Math.Abs(cfv) > 0 ? (ceEma - cfv) / Math.Abs(cfv) * 100m : null;
            decimal? peFrozenPct = state.PeFrozenValue is { } pfv && Math.Abs(pfv) > 0 ? (peEma - pfv) / Math.Abs(pfv) * 100m : null;
            var ceFcHit = ceFrozenPct is { } cfp && Math.Abs(cfp) >= _options.FrozenPctThreshold;
            var peFcHit = peFrozenPct is { } pfp && Math.Abs(pfp) >= _options.FrozenPctThreshold;

            var multiLookback = _options.MultiLookbackBars.Select(bars =>
            {
                var (ce, pe) = LookbackChange(ceEmaSeries, peEmaSeries, bars);
                return new CdvLookbackChange(
                    bars, ce, pe,
                    ce is { } c && Math.Abs(c) >= _options.LookbackPctThreshold,
                    pe is { } p && Math.Abs(p) >= _options.LookbackPctThreshold);
            }).ToArray();

            decimal? cePrevDayClose = state.PreviousDayCe.Count > 0 ? state.PreviousDayCe[^1] : null;
            decimal? pePrevDayClose = state.PreviousDayPe.Count > 0 ? state.PreviousDayPe[^1] : null;
            decimal? ceDayChangePct = cePrevDayClose is { } cpd && Math.Abs(cpd) > 0 ? (ceEma - cpd) / Math.Abs(cpd) * 100m : null;
            decimal? peDayChangePct = pePrevDayClose is { } ppd && Math.Abs(ppd) > 0 ? (peEma - ppd) / Math.Abs(ppd) * 100m : null;

            var snapshot = new CdvSnapshot(
                state.Underlying, nowUtc, state.Atm, state.StrikeStep, state.Expiry, state.LastSpot,
                ceSeries[lastIndex], peSeries[lastIndex], ceEma, peEma, barCount,
                ceLbPct, peLbPct, ceLbHit, peLbHit,
                state.CeFrozenValue, state.PeFrozenValue, ceFrozenPct, peFrozenPct, ceFcHit, peFcHit,
                multiLookback, crossUp, crossDown,
                cePrevDayClose, pePrevDayClose, ceDayChangePct, peDayChangePct);

            _ = _publisher.PublishAsync(snapshot, CancellationToken.None);
        }
    }

    /// <summary>Standard EMA, seeded at the series' first value and replayed forward - always run
    /// over the FULL bar series (never carried incrementally), so a re-centre that changes which
    /// strikes make up ceSeries/peSeries produces a correctly-recomputed EMA rather than one that
    /// silently blends pre- and post-recentre composition.</summary>
    private static (decimal[] Ce, decimal[] Pe) ComputeEma(decimal[] ceSeries, decimal[] peSeries, int length)
    {
        var alpha = 2m / (length + 1);
        var ce = new decimal[ceSeries.Length];
        var pe = new decimal[peSeries.Length];
        for (var i = 0; i < ceSeries.Length; i++)
        {
            ce[i] = i == 0 ? ceSeries[0] : ce[i - 1] + alpha * (ceSeries[i] - ce[i - 1]);
            pe[i] = i == 0 ? peSeries[0] : pe[i - 1] + alpha * (peSeries[i] - pe[i - 1]);
        }
        return (ce, pe);
    }

    private static (decimal? Ce, decimal? Pe) LookbackChange(decimal[] ceEma, decimal[] peEma, int bars)
    {
        var idx = ceEma.Length - 1 - bars;
        if (idx < 0) return (null, null);

        decimal? cePct = Math.Abs(ceEma[idx]) > 0 ? (ceEma[^1] - ceEma[idx]) / Math.Abs(ceEma[idx]) * 100m : null;
        decimal? pePct = Math.Abs(peEma[idx]) > 0 ? (peEma[^1] - peEma[idx]) / Math.Abs(peEma[idx]) * 100m : null;
        return (cePct, pePct);
    }

    /// <summary>True if `nowUtc`'s bar is the first one at/after the configured IST clock time
    /// today - mirrors Pine's isFrozenTime (barOpenMs <= targetMs < barOpenMs + tfMs).</summary>
    private static bool IsFrozenTime(DateTimeOffset nowUtc, string frozenTimeIst, int timeframeSeconds)
    {
        if (!TimeSpan.TryParse(frozenTimeIst, out var target)) return false;
        var nowIst = TimeZoneInfo.ConvertTime(nowUtc, Ist);
        var barOpen = nowIst.TimeOfDay - TimeSpan.FromSeconds(Math.Max(5, timeframeSeconds));
        return barOpen <= target && target < nowIst.TimeOfDay;
    }

    private static long StrikeKey(int strike, bool isCall) => (long)strike * 10 + (isCall ? 1 : 0);

    private static TimeZoneInfo ResolveIst()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "IST");
    }

    private sealed class UnderlyingState(string underlying, int strikeStep, DateOnly expiry, DateOnly tradingDay)
    {
        public readonly string Underlying = underlying;
        public readonly int StrikeStep = strikeStep;
        public DateOnly Expiry = expiry;
        public readonly DateOnly TradingDay = tradingDay;
        public int Atm;
        public uint SpotToken;
        public decimal LastSpot;
        public DateTimeOffset LastAtmRecenterUtc;
        public readonly Dictionary<long, StrikeRuntime> Strikes = new();
        public decimal? PrevCeEma;
        public decimal? PrevPeEma;
        public decimal? CeFrozenValue;
        public decimal? PeFrozenValue;
        public bool FrozenCaptured;

        // Previous trading day's reconstructed CDV curve (see BackfillPreviousDayAsync) - built once
        // per trading day, from Dhan's historical intraday candles via Pine's own bar-proxy formula
        // (historical candles carry no real buy/sell tagging, the same limitation Pine has). Empty
        // until the backfill completes; PreviousDayBackfillAttempted guards against retrying it every
        // cycle regardless of outcome.
        public readonly List<decimal> PreviousDayCe = new();
        public readonly List<decimal> PreviousDayPe = new();
        public bool PreviousDayBackfillAttempted;
    }

    private sealed class StrikeRuntime(uint token, int strike, bool isCall)
    {
        public readonly uint Token = token;
        public readonly decimal Strike = strike;
        public readonly bool IsCall = isCall;
        public decimal RunningCdv;
        public readonly List<decimal> BarValues = new();
    }

    /// <summary>ExpectedExchange is the canonical exchange (BrokerInstrument.Exchange - "IDX",
    /// "NFO", ...) this token was resolved under, checked against Tick.Segment in OnTick before the
    /// tick is trusted - see Tick.Segment's doc comment for why a bare token isn't safe to trust on
    /// its own on Dhan.</summary>
    private sealed record TokenTarget(UnderlyingState State, StrikeRuntime? Strike, string ExpectedExchange);
}
