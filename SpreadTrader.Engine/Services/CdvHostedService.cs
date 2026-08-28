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
    private readonly CdvOptions _options;
    private readonly ILogger<CdvHostedService> _logger;

    // Only ever mutated from the single ExecuteAsync/PeriodicTimer loop (never reentrant), so a
    // plain Dictionary is safe.
    private readonly Dictionary<string, UnderlyingState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedUnderlyings = new(StringComparer.OrdinalIgnoreCase);

    // Written from the ExecuteAsync loop (EnsureChainAsync) but READ from OnTick, which runs on the
    // feed's own receive-loop thread (see IMarketDataFeed.TickReceived's doc comment) - genuinely
    // concurrent with the timer loop, so this has to be thread-safe, unlike the two fields above.
    private readonly ConcurrentDictionary<uint, TokenTarget> _tokenIndex = new();

    public CdvHostedService(
        IOptionChainSource instrumentMaster,
        IMarketDataFeed marketDataFeed,
        ITickStore tickStore,
        ICdvPublisher publisher,
        CdvTokenRegistry tokenRegistry,
        IInstrumentSegmentRegistry segmentRegistry,
        IOptions<CdvOptions> options,
        ILogger<CdvHostedService> logger)
    {
        _instrumentMaster = instrumentMaster;
        _marketDataFeed = marketDataFeed;
        _tickStore = tickStore;
        _publisher = publisher;
        _tokenRegistry = tokenRegistry;
        _segmentRegistry = segmentRegistry;
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

        if (_options.TimeframeSeconds < 5)
        {
            _logger.LogWarning("Cdv:TimeframeSeconds ({Value}) is too low - clamping to 5s.", _options.TimeframeSeconds);
        }

        _marketDataFeed.TickReceived += OnTick;
        try
        {
            await _instrumentMaster.EnsureLoadedAsync(stoppingToken);

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

        if (!_tickStore.TryGet(spot.InstrumentToken, out var spotTick) || SpotPrice(spotTick) <= 0)
        {
            // Spot hasn't ticked yet this process lifetime - subscribe it now (harmless if already
            // subscribed) and try again next cycle once a tick has actually arrived.
            await _marketDataFeed.SubscribeAsync(new[] { spot.InstrumentToken }, stoppingToken);
            return state;
        }

        var spotPrice = SpotPrice(spotTick);
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
        _tokenIndex[spot.InstrumentToken] = new TokenTarget(state, null);

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
                _tokenIndex[contract.InstrumentToken] = new TokenTarget(state, runtime);
                toSubscribe.Add(contract.InstrumentToken);
            }
        }

        if (toSubscribe.Count > 0)
            await _marketDataFeed.SubscribeAsync(toSubscribe, stoppingToken);

        return state;
    }

    private static decimal SpotPrice(Tick tick) =>
        tick.LastPrice > 0 ? tick.LastPrice : tick.BestAskPrice > 0 ? tick.BestAskPrice : tick.BestBidPrice;

    private void OnTick(Tick tick)
    {
        if (!_tokenIndex.TryGetValue(tick.InstrumentToken, out var target)) return;

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

            var snapshot = new CdvSnapshot(
                state.Underlying, nowUtc, state.Atm, state.StrikeStep, state.Expiry, state.LastSpot,
                ceSeries[lastIndex], peSeries[lastIndex], ceEma, peEma, barCount,
                ceLbPct, peLbPct, ceLbHit, peLbHit,
                state.CeFrozenValue, state.PeFrozenValue, ceFrozenPct, peFrozenPct, ceFcHit, peFcHit,
                multiLookback, crossUp, crossDown);

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
    }

    private sealed class StrikeRuntime(uint token, int strike, bool isCall)
    {
        public readonly uint Token = token;
        public readonly decimal Strike = strike;
        public readonly bool IsCall = isCall;
        public decimal RunningCdv;
        public readonly List<decimal> BarValues = new();
    }

    private sealed record TokenTarget(UnderlyingState State, StrikeRuntime? Strike);
}
