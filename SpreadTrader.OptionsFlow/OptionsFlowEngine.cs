using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.OptionsFlow;

/// <summary>
/// Computes the aggregated CE/PE flow series (CDV and OBV) for each configured index, over an
/// ATM-centred window of option strikes.
///
/// THE SERIES IS ALWAYS A CONSISTENT SUM OVER THE CURRENT WINDOW. That single rule explains most
/// of the design. CDV and OBV are cumulative sums across a SET of contracts, so when ATM moves and
/// the window re-centres, the set changes - and splicing the new set's numbers onto the old set's
/// running total puts a step in the curve that reads exactly like a real flow event but is pure
/// bookkeeping. So a shift does not splice: it re-fetches whatever history the newly included
/// strikes are missing and recomputes the entire day from the session open over the new window.
/// Every point plotted is therefore the sum over the strikes currently tracked, with no artificial
/// discontinuity anywhere.
///
/// EVERYTHING IS MINUTE BARS, with only the newest one live. Broker intraday history has a
/// one-minute floor (see IOptionHistoryProvider), and a series that must be rebuildable from the
/// session open can only be as fine as the history that rebuilds it. The still-open minute is
/// recomputed from the tick store on every refresh, so the tip of the curve moves in real time
/// while every closed bar stays fixed.
///
/// This type owns no threads and no timers - OptionsFlowHostedService drives it. It is safe to
/// read snapshots from other threads (controllers, SignalR) while a refresh is running.
/// </summary>
public sealed class OptionsFlowEngine : IExtraTickSubscriber
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private readonly OptionsFlowOptions _options;
    private readonly IOptionsInstrumentSource _instruments;
    private readonly IOptionHistoryProvider _history;
    private readonly IBrokerInstrumentMaster _brokerInstruments;
    private readonly IMarketDataFeed _feed;
    private readonly ITickStore _tickStore;
    private readonly ILogger<OptionsFlowEngine> _logger;

    private readonly ConcurrentDictionary<string, IndexFlowState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Published snapshots, replaced wholesale on each refresh so readers never observe a
    /// half-updated series.</summary>
    private volatile IReadOnlyDictionary<string, OptionsFlowSnapshot> _snapshots =
        new Dictionary<string, OptionsFlowSnapshot>(StringComparer.OrdinalIgnoreCase);

    public OptionsFlowEngine(
        IOptions<OptionsFlowOptions> options,
        IOptionsInstrumentSource instruments,
        IOptionHistoryProvider history,
        IBrokerInstrumentMaster brokerInstruments,
        IMarketDataFeed feed,
        ITickStore tickStore,
        ILogger<OptionsFlowEngine> logger)
    {
        _options = options.Value;
        _instruments = instruments;
        _history = history;
        _brokerInstruments = brokerInstruments;
        _feed = feed;
        _tickStore = tickStore;
        _logger = logger;
    }

    /// <summary>Latest computed snapshot per index, for the controller and the SignalR push.</summary>
    public IReadOnlyDictionary<string, OptionsFlowSnapshot> Snapshots => _snapshots;

    public OptionsFlowSnapshot? GetSnapshot(string index) =>
        _snapshots.TryGetValue(index, out var snapshot) ? snapshot : null;

    /// <summary>Every option token currently tracked, plus each index's spot token - all of which
    /// must stay subscribed. See IExtraTickSubscriber for why this is needed at all.</summary>
    public IReadOnlyCollection<uint> GetRequiredTokens()
    {
        var tokens = new HashSet<uint>();
        foreach (var state in _states.Values)
        {
            if (state.SpotToken != 0) tokens.Add(state.SpotToken);
            foreach (var contract in state.Window) tokens.Add(contract.InstrumentToken);
        }
        return tokens;
    }

    // ---- Refresh cycle ---------------------------------------------------------------------------

    /// <summary>
    /// One full cycle: make sure each index has a window (re-centring it if ATM has moved and the
    /// shift interval has elapsed), fold the latest ticks into the open minute, and recompute every
    /// series. Called on the engine's timer; never throws for one index's sake - a failure is
    /// logged and the other indices still update.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _instruments.EnsureLoadedAsync(cancellationToken);

        var snapshots = new Dictionary<string, OptionsFlowSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in _options.ResolveIndices())
        {
            try
            {
                var state = _states.GetOrAdd(index, i => new IndexFlowState(i));
                await EnsureWindowAsync(state, cancellationToken);

                if (state.Window.Count == 0) continue;

                UpdateLiveBars(state);

                var snapshot = BuildSnapshot(state);
                if (snapshot is not null) snapshots[index] = snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Options Flow: refresh failed for {Index} - other indices are unaffected.", index);
                // Keep the previous snapshot for this index rather than blanking the chart over a
                // transient failure.
                if (_snapshots.TryGetValue(index, out var previous)) snapshots[index] = previous;
            }
        }

        _snapshots = snapshots;
    }

    /// <summary>
    /// Builds the strike window if there isn't one, or re-centres it when ATM has moved and the
    /// configured shift interval has elapsed. A re-centre subscribes the newly included strikes,
    /// unsubscribes the dropped ones, backfills whatever history the new ones are missing, and
    /// leaves the retained strikes' cached bars untouched - so a one-strike move costs two history
    /// calls, not a whole window's worth.
    /// </summary>
    private async Task EnsureWindowAsync(IndexFlowState state, CancellationToken cancellationToken)
    {
        var spot = ResolveSpot(state);
        if (spot <= 0)
        {
            if (state.Window.Count == 0)
                _logger.LogDebug("Options Flow: no spot price for {Index} yet - waiting for its first tick.", state.Index);
            return;
        }

        state.Spot = spot;

        var expiry = _instruments.GetNearestExpiry(state.Index);
        if (expiry is null)
        {
            _logger.LogWarning("Options Flow: no unexpired option chain found for {Index}.", state.Index);
            return;
        }

        var chain = _instruments.GetChain(state.Index, expiry.Value);
        if (chain.Count == 0) return;

        var strikeStep = ResolveStrikeStep(chain);
        if (strikeStep <= 0) return;

        var atm = Math.Round(spot / strikeStep, MidpointRounding.AwayFromZero) * strikeStep;

        var isFirstBuild = state.Window.Count == 0;
        var expiryRolled = state.Expiry != expiry.Value;
        var shiftDue = _options.AtmShiftIntervalMinutes > 0
                       && DateTimeOffset.UtcNow - state.WindowSetAtUtc >= TimeSpan.FromMinutes(_options.AtmShiftIntervalMinutes);

        if (!isFirstBuild && !expiryRolled && !(shiftDue && atm != state.AtmStrike)) return;

        var desired = SelectWindow(chain, atm, strikeStep);
        if (desired.Count == 0)
        {
            _logger.LogWarning("Options Flow: {Index} chain has no strikes around ATM {Atm}.", state.Index, atm);
            return;
        }

        var previousTokens = state.Window.Select(c => c.InstrumentToken).ToHashSet();
        var desiredTokens = desired.Select(c => c.InstrumentToken).ToHashSet();

        var added = desired.Where(c => !previousTokens.Contains(c.InstrumentToken)).ToArray();
        var removed = previousTokens.Where(t => !desiredTokens.Contains(t)).ToArray();

        _logger.LogInformation(
            "Options Flow: {Action} {Index} window on ATM {Atm} (spot {Spot:F2}, expiry {Expiry}) - {Total} contracts, {Added} added, {Removed} dropped.",
            isFirstBuild ? "building" : "re-centring", state.Index, atm, spot, expiry.Value, desired.Count, added.Length, removed.Length);

        state.Window = desired;
        state.AtmStrike = atm;
        state.Expiry = expiry.Value;
        state.WindowSetAtUtc = DateTimeOffset.UtcNow;

        foreach (var token in removed)
        {
            state.Bars.Remove(token);
            state.LiveBars.Remove(token);
        }

        // Subscribe first so ticks start flowing while history is being fetched.
        var toSubscribe = added.Select(c => c.InstrumentToken).ToList();
        if (state.SpotToken != 0) toSubscribe.Add(state.SpotToken);
        if (toSubscribe.Count > 0)
            await _feed.SubscribeAsync(toSubscribe, cancellationToken);

        if (removed.Length > 0)
            await _feed.UnsubscribeAsync(removed, cancellationToken);

        await BackfillAsync(state, added, cancellationToken);
    }

    /// <summary>
    /// Seeds each newly tracked contract's bars from the session open. Without this the series
    /// could only start when the process did, and an ATM shift could not be rebuilt at all - see
    /// the class doc comment.
    /// </summary>
    private async Task BackfillAsync(IndexFlowState state, IReadOnlyList<OptionContract> contracts, CancellationToken cancellationToken)
    {
        if (contracts.Count == 0) return;

        if (!_options.BackfillFromSessionStart || !_history.IsAvailable)
        {
            if (contracts.Count > 0 && state.Backfilled)
                state.Backfilled = false;

            _logger.LogInformation(
                "Options Flow: history backfill unavailable for the active broker - {Index} will accumulate from now instead of the session open.",
                state.Index);
            return;
        }

        var from = SessionStartUtc();
        var to = DateTimeOffset.UtcNow;
        var loaded = 0;

        foreach (var contract in contracts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bars = await _history.GetMinuteBarsAsync(contract, from, to, cancellationToken);
            if (bars.Count == 0) continue;

            var series = state.Bars.TryGetValue(contract.InstrumentToken, out var existing)
                ? existing
                : state.Bars[contract.InstrumentToken] = new SortedDictionary<long, OptionBar>();

            foreach (var bar in bars)
                series[bar.MinuteUtc.ToUnixTimeSeconds()] = bar;

            loaded++;
        }

        state.Backfilled = true;
        _logger.LogInformation("Options Flow: backfilled {Loaded}/{Total} newly tracked {Index} contracts from {From:HH:mm} IST.",
            loaded, contracts.Count, state.Index, from.ToOffset(IstOffset));
    }

    // ---- Live bars -------------------------------------------------------------------------------

    /// <summary>
    /// Folds the latest tick for every tracked contract into its open minute bar. The broker feed
    /// reports the day's RUNNING volume, so this bar's own volume is the difference from what the
    /// running total was when the minute began - that difference is what the flow maths needs.
    /// </summary>
    private void UpdateLiveBars(IndexFlowState state)
    {
        var nowMinute = FloorToMinute(DateTimeOffset.UtcNow);

        foreach (var contract in state.Window)
        {
            if (!_tickStore.TryGet(contract.InstrumentToken, out var tick)) continue;
            if (tick.LastPrice <= 0) continue;

            var series = state.Bars.TryGetValue(contract.InstrumentToken, out var existing)
                ? existing
                : state.Bars[contract.InstrumentToken] = new SortedDictionary<long, OptionBar>();

            if (!state.LiveBars.TryGetValue(contract.InstrumentToken, out var live) || live.MinuteUtc != nowMinute)
            {
                // Rolling into a new minute. The previous minute's bar is already in `series` and
                // simply stops being touched from here, which is what makes closed bars immutable.
                live = new LiveBar
                {
                    MinuteUtc = nowMinute,
                    Open = tick.LastPrice,
                    High = tick.LastPrice,
                    Low = tick.LastPrice,
                    Close = tick.LastPrice,
                    DayVolumeAtStart = tick.Volume,
                };
                state.LiveBars[contract.InstrumentToken] = live;
            }

            live.High = Math.Max(live.High, tick.LastPrice);
            live.Low = live.Low <= 0 ? tick.LastPrice : Math.Min(live.Low, tick.LastPrice);
            live.Close = tick.LastPrice;

            // A day-volume counter that went backwards means the feed reset (reconnect, or a new
            // session): re-anchor rather than emit a negative volume.
            if (tick.Volume < live.DayVolumeAtStart) live.DayVolumeAtStart = tick.Volume;

            series[nowMinute.ToUnixTimeSeconds()] = new OptionBar(
                nowMinute, live.Open, live.High, live.Low, live.Close, tick.Volume - live.DayVolumeAtStart);
        }
    }

    // ---- Aggregation -----------------------------------------------------------------------------

    /// <summary>
    /// Sums every tracked contract's per-bar flow into one CE and one PE cumulative series, then
    /// smooths both. Recomputed in full each cycle rather than updated incrementally: the whole day
    /// is only a few hundred minutes across a few dozen contracts, and recomputing means an ATM
    /// shift needs no special path at all - the new window simply produces a new, internally
    /// consistent curve.
    /// </summary>
    private OptionsFlowSnapshot? BuildSnapshot(IndexFlowState state)
    {
        var sessionStart = SessionStartUtc().ToUnixTimeSeconds();
        var sessionEnd = SessionEndUtc().ToUnixTimeSeconds();

        var call = BuildSide(state, OptionRight.Call, sessionStart, sessionEnd);
        var put = BuildSide(state, OptionRight.Put, sessionStart, sessionEnd);

        if (call.Count == 0 && put.Count == 0) return null;

        return new OptionsFlowSnapshot(
            Index: state.Index,
            AtmStrike: state.AtmStrike,
            Spot: state.Spot,
            Expiry: state.Expiry,
            StrikeCount: state.Window.Count(c => c.Right == OptionRight.Call),
            WindowSetAtUtc: state.WindowSetAtUtc,
            DeltaWeighted: _options.UseDeltaWeighting,
            Backfilled: state.Backfilled,
            Call: call,
            Put: put);
    }

    private IReadOnlyList<OptionFlowPoint> BuildSide(IndexFlowState state, OptionRight right, long sessionStart, long sessionEnd)
    {
        var contracts = state.Window.Where(c => c.Right == right).ToArray();
        if (contracts.Length == 0) return Array.Empty<OptionFlowPoint>();

        // Every minute any tracked contract has a bar for, in order.
        var minutes = new SortedSet<long>();
        foreach (var contract in contracts)
        {
            if (!state.Bars.TryGetValue(contract.InstrumentToken, out var series)) continue;
            foreach (var minute in series.Keys)
                if (minute >= sessionStart && minute <= sessionEnd) minutes.Add(minute);
        }

        if (minutes.Count == 0) return Array.Empty<OptionFlowPoint>();

        // Delta weight is a function of strike and the CURRENT spot, matching the reference script,
        // which recomputes it live rather than per historical bar.
        var weights = contracts.ToDictionary(
            c => c.InstrumentToken,
            c => OptionFlowMath.DeltaWeight(c.Strike, right == OptionRight.Call, state.Spot, _options.UseDeltaWeighting));

        var previousCloses = new Dictionary<uint, decimal>();
        var cdvRaw = new List<decimal>(minutes.Count);
        var obvRaw = new List<decimal>(minutes.Count);
        var stamps = new List<DateTimeOffset>(minutes.Count);

        decimal cdvRunning = 0, obvRunning = 0;

        foreach (var minute in minutes)
        {
            decimal cdvStep = 0, obvStep = 0;

            foreach (var contract in contracts)
            {
                if (!state.Bars.TryGetValue(contract.InstrumentToken, out var series)) continue;
                if (!series.TryGetValue(minute, out var bar)) continue;

                var weight = weights[contract.InstrumentToken];
                cdvStep += OptionFlowMath.BarVolumeDelta(bar, weight);

                if (previousCloses.TryGetValue(contract.InstrumentToken, out var previousClose))
                    obvStep += OptionFlowMath.BarObv(bar, previousClose, weight);

                previousCloses[contract.InstrumentToken] = bar.Close;
            }

            cdvRunning += cdvStep;
            obvRunning += obvStep;

            stamps.Add(DateTimeOffset.FromUnixTimeSeconds(minute));
            cdvRaw.Add(cdvRunning);
            obvRaw.Add(obvRunning);
        }

        var cdv = OptionFlowMath.Ema(cdvRaw, _options.SmoothingLength);
        var obv = OptionFlowMath.Ema(obvRaw, _options.SmoothingLength);

        var points = new OptionFlowPoint[stamps.Count];
        for (var i = 0; i < stamps.Count; i++)
            points[i] = new OptionFlowPoint(stamps[i], cdv[i], obv[i]);

        return points;
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>The index level the ATM is derived from, read from the live tick for the index's
    /// spot instrument. Resolved once per index and remembered.</summary>
    private decimal ResolveSpot(IndexFlowState state)
    {
        if (state.SpotToken == 0)
        {
            var spotInstrument = _brokerInstruments.FindSpot(state.Index);
            if (spotInstrument is null) return 0m;
            state.SpotToken = spotInstrument.InstrumentToken;
        }

        return _tickStore.TryGet(state.SpotToken, out var tick) ? tick.LastPrice : state.Spot;
    }

    /// <summary>Derives the chain's strike spacing from the chain itself (the smallest gap between
    /// adjacent listed strikes) rather than hardcoding 50 for NIFTY and 100 for BANKNIFTY - the
    /// exchange has changed these before, and a stale constant would silently centre the window on
    /// the wrong strikes.</summary>
    private static decimal ResolveStrikeStep(IReadOnlyList<OptionContract> chain)
    {
        var strikes = chain.Select(c => c.Strike).Distinct().OrderBy(s => s).ToArray();
        if (strikes.Length < 2) return 0m;

        var step = decimal.MaxValue;
        for (var i = 1; i < strikes.Length; i++)
        {
            var gap = strikes[i] - strikes[i - 1];
            if (gap > 0 && gap < step) step = gap;
        }

        return step == decimal.MaxValue ? 0m : step;
    }

    /// <summary>The ATM-centred strike window, both rights. A strike is only included when BOTH its
    /// CE and PE are listed, so the two series always cover the same strikes and can be compared
    /// against each other.</summary>
    private IReadOnlyList<OptionContract> SelectWindow(IReadOnlyList<OptionContract> chain, decimal atm, decimal step)
    {
        var wanted = new HashSet<decimal>();
        for (var i = -_options.StrikesBelowAtm; i <= _options.StrikesAboveAtm; i++)
            wanted.Add(atm + i * step);

        var byStrike = chain.Where(c => wanted.Contains(c.Strike))
            .GroupBy(c => c.Strike)
            .Where(g => g.Any(c => c.Right == OptionRight.Call) && g.Any(c => c.Right == OptionRight.Put));

        return byStrike.SelectMany(g => g).OrderBy(c => c.Strike).ThenBy(c => c.Right).ToArray();
    }

    private DateTimeOffset SessionStartUtc() => SessionBoundaryUtc(_options.SessionStartIst, 9, 15);

    private DateTimeOffset SessionEndUtc() => SessionBoundaryUtc(_options.SessionEndIst, 15, 30);

    /// <summary>Today's session boundary, as a UTC instant. "Today" is the IST calendar date, so a
    /// process running past midnight UTC (which is 05:30 IST, before the open) still anchors to the
    /// right trading day.</summary>
    private static DateTimeOffset SessionBoundaryUtc(string istTime, int fallbackHour, int fallbackMinute)
    {
        var hour = fallbackHour;
        var minute = fallbackMinute;

        var parts = istTime?.Split(':');
        if (parts is { Length: 2 } && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
        {
            hour = h;
            minute = m;
        }

        var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
        return new DateTimeOffset(nowIst.Year, nowIst.Month, nowIst.Day, hour, minute, 0, IstOffset).ToUniversalTime();
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero);

    // ---- State -----------------------------------------------------------------------------------

    private sealed class IndexFlowState
    {
        public IndexFlowState(string index) => Index = index;

        public string Index { get; }
        public uint SpotToken { get; set; }
        public decimal Spot { get; set; }
        public decimal AtmStrike { get; set; }
        public DateOnly Expiry { get; set; }
        public DateTimeOffset WindowSetAtUtc { get; set; } = DateTimeOffset.MinValue;
        public bool Backfilled { get; set; }
        public IReadOnlyList<OptionContract> Window { get; set; } = Array.Empty<OptionContract>();

        /// <summary>Minute bars per contract, keyed by minute epoch seconds. Seeded by backfill,
        /// extended live.</summary>
        public Dictionary<uint, SortedDictionary<long, OptionBar>> Bars { get; } = new();

        public Dictionary<uint, LiveBar> LiveBars { get; } = new();
    }

    private sealed class LiveBar
    {
        public DateTimeOffset MinuteUtc { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long DayVolumeAtStart { get; set; }
    }
}
