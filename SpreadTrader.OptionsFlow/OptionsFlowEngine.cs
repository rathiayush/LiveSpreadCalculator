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
    /// <summary>Same Telegram-backed "send this text somewhere" abstraction PnlAlertHostedService
    /// uses (see IPnlAlertNotifier's doc comment) - reused rather than inventing a second alert
    /// channel, so an option-flow alert shows up exactly where a PNL alert already does.</summary>
    private readonly IPnlAlertNotifier _notifier;
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
        IPnlAlertNotifier notifier,
        ILogger<OptionsFlowEngine> logger)
    {
        _options = options.Value;
        _instruments = instruments;
        _history = history;
        _brokerInstruments = brokerInstruments;
        _feed = feed;
        _tickStore = tickStore;
        _notifier = notifier;
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

                // Tracked independently of the option chain window below - the spot chart itself
                // is what the "9/21 EMA crossover, not CDV/OBV" alert watches, so it needs to start
                // accumulating the moment a spot tick is available, even before the strike window
                // (and therefore the CE/PE series) has resolved.
                UpdateSpotLiveBar(state);

                if (state.Window.Count == 0) continue;

                UpdateLiveBars(state);

                var snapshot = BuildSnapshot(state);
                if (snapshot is not null)
                {
                    snapshots[index] = snapshot;
                    await DispatchAlertsAsync(state, snapshot, cancellationToken);
                }
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

    /// <summary>
    /// Folds the index's current spot tick into its own open minute bar, exactly like
    /// UpdateLiveBars does per option contract - just a single series instead of one per strike.
    /// This is what AddSpotEmaCross's 9/21 EMA crossover is computed over.
    ///
    /// Unlike the option contracts, there is no backfill here (IOptionHistoryProvider serves option
    /// contract history, not index history), so this series - and therefore the spot EMA crossover
    /// alert - only ever covers "since the feature started accumulating", same degraded behaviour
    /// the option series falls back to when backfill is unavailable. Volume is meaningless for an
    /// index (nothing trades it directly) so it is always recorded as 0 - only Close is used.
    /// </summary>
    private static void UpdateSpotLiveBar(IndexFlowState state)
    {
        if (state.Spot <= 0) return;

        var minute = FloorToMinute(DateTimeOffset.UtcNow);
        var key = minute.ToUnixTimeSeconds();

        var bar = state.SpotBars.TryGetValue(key, out var existing)
            ? existing
            : new OptionBar(minute, state.Spot, state.Spot, state.Spot, state.Spot, 0);

        state.SpotBars[key] = bar with
        {
            High = Math.Max(bar.High, state.Spot),
            Low = bar.Low <= 0 ? state.Spot : Math.Min(bar.Low, state.Spot),
            Close = state.Spot,
        };
    }

    /// <summary>
    /// Sends a Telegram notification for every signal OptionsFlowAlertGate reports as newly
    /// alert-worthy this cycle. One index's send failure (Telegram down, etc.) is logged and does
    /// not stop the others - the gate has already recorded the signal as alerted either way, so a
    /// failed send is dropped rather than retried next cycle (the same "log and move on" choice
    /// PnlAlertHostedService makes for its own sends).
    /// </summary>
    private async Task DispatchAlertsAsync(IndexFlowState state, OptionsFlowSnapshot snapshot, CancellationToken cancellationToken)
    {
        var alerts = state.AlertGate.SelectNewAlerts(snapshot.Signals);

        foreach (var signal in alerts)
        {
            try
            {
                await _notifier.SendAsync(FormatAlertText(state.Index, signal), cancellationToken);
                _logger.LogInformation(
                    "Options Flow: alert fired for {Index} - {Kind} {Metric} {Scope}.",
                    state.Index, signal.Kind, signal.Metric, signal.Scope);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Options Flow: failed to send alert for {Index} - {Kind} {Metric} {Scope}.",
                    state.Index, signal.Kind, signal.Metric, signal.Scope);
            }
        }
    }

    private string FormatAlertText(string index, OptionFlowSignal signal) => signal.Kind switch
    {
        "LookbackChange" =>
            $"\U0001F4C8 <b>Options Flow - {index}</b>\n" +
            $"{signal.Scope} {signal.Metric} moved {signal.ChangePercent:+0.#;-0.#}% over the last {signal.LookbackBars} bars.",

        "CePeCross" =>
            $"\U0001F501 <b>Options Flow - {index}</b>\n" +
            $"{signal.Metric}: {(signal.Bullish ? "CE crossed above PE" : "PE crossed above CE")}.",

        "PriceEmaCross" =>
            $"\U0001F4C9 <b>Options Flow - {index}</b>\n" +
            $"Spot chart EMA {_options.SignalFastEmaLength}/{_options.SignalSlowEmaLength} crossed " +
            $"{(signal.Bullish ? "bullish (fast above slow)" : "bearish (fast below slow)")}.",

        _ => $"Options Flow - {index}: {signal.Kind} {signal.Metric} {signal.Scope}.",
    };

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

        if (call.Points.Count == 0 && put.Points.Count == 0) return null;

        var signals = BuildSignals(state, call, put, sessionStart, sessionEnd);

        return new OptionsFlowSnapshot(
            Index: state.Index,
            AtmStrike: state.AtmStrike,
            Spot: state.Spot,
            Expiry: state.Expiry,
            StrikeCount: state.Window.Count(c => c.Right == OptionRight.Call),
            WindowSetAtUtc: state.WindowSetAtUtc,
            DeltaWeighted: _options.UseDeltaWeighting,
            Backfilled: state.Backfilled,
            Call: call.Points,
            Put: put.Points,
            Signals: signals);
    }

    /// <summary>
    /// Builds every signal the dashboard's signal table reports, and that OptionsFlowAlertGate then
    /// filters for alert-worthiness (see DispatchAlertsAsync):
    ///  - a fast/slow EMA cross on each side's CDV/OBV independently (display-only - not alerted)
    ///  - the CE-vs-PE cross on CDV and OBV ("CePeCross" - alerted)
    ///  - the 5/10/15-bar % change on CDV/OBV past threshold ("LookbackChange" - alerted)
    ///  - the 9/21 EMA crossover on the index's own spot PRICE chart ("PriceEmaCross" - alerted;
    ///    deliberately NOT computed from CDV/OBV, see AddSpotEmaCross)
    ///
    /// The EMAs on CDV/OBV are taken over the RAW cumulative series rather than the smoothed line
    /// that gets plotted - see OptionsFlowOptions.SignalFastEmaLength for why (an EMA of an EMA
    /// would report every cross materially late).
    /// </summary>
    private IReadOnlyList<OptionFlowSignal> BuildSignals(IndexFlowState state, SideSeries call, SideSeries put, long sessionStart, long sessionEnd)
    {
        var signals = new List<OptionFlowSignal>(12);

        AddEmaCross(signals, "CDV", "CE", call.Stamps, call.RawCdv);
        AddEmaCross(signals, "CDV", "PE", put.Stamps, put.RawCdv);
        AddEmaCross(signals, "OBV", "CE", call.Stamps, call.RawObv);
        AddEmaCross(signals, "OBV", "PE", put.Stamps, put.RawObv);

        AddSeriesCross(signals, "CDV", call, put, c => c.RawCdv);
        AddSeriesCross(signals, "OBV", call, put, c => c.RawObv);

        AddLookbackAlerts(signals, "CDV", "CE", call.Stamps, call.RawCdv);
        AddLookbackAlerts(signals, "CDV", "PE", put.Stamps, put.RawCdv);
        AddLookbackAlerts(signals, "OBV", "CE", call.Stamps, call.RawObv);
        AddLookbackAlerts(signals, "OBV", "PE", put.Stamps, put.RawObv);

        AddSpotEmaCross(signals, state, sessionStart, sessionEnd);

        return signals;
    }

    /// <summary>
    /// The "N-bar % change" alert: for each configured lookback (5/10/15 by default), how much the
    /// raw cumulative CDV/OBV has moved since that many bars ago - see
    /// OptionFlowMath.LookbackChangePercent. Every lookback that clears
    /// OptionsFlowOptions.LookbackAlertThresholdPercent gets its own signal (a metric can be
    /// simultaneously past the 5-bar AND 10-bar threshold, and both are reported).
    /// </summary>
    private void AddLookbackAlerts(List<OptionFlowSignal> signals, string metric, string scope,
        IReadOnlyList<DateTimeOffset> stamps, IReadOnlyList<decimal> raw)
    {
        if (raw.Count == 0 || stamps.Count == 0) return;

        var last = raw.Count - 1;

        foreach (var lookback in _options.LookbackAlertBars)
        {
            var pct = OptionFlowMath.LookbackChangePercent(raw, lookback);
            if (pct is not { } percent) continue;
            if (Math.Abs(percent) < _options.LookbackAlertThresholdPercent) continue;

            signals.Add(new OptionFlowSignal(
                Kind: "LookbackChange",
                Metric: metric,
                Scope: scope,
                Bullish: percent >= 0,
                AtUtc: stamps[last],
                BarsAgo: lookback,
                Fast: raw[last],
                Slow: raw[last - lookback],
                LookbackBars: lookback,
                ChangePercent: percent));
        }
    }

    /// <summary>
    /// The "9/21 EMA crossover, not CDV/OBV" alert: a plain fast/slow EMA cross computed on the
    /// index's own spot PRICE series (see UpdateSpotLiveBar), independent of the option chain
    /// entirely - deliberately reusing the same DetectCross machinery as the CDV/OBV signals, just
    /// fed price instead of flow. One signal per index (Scope "Index" - there is only one spot
    /// line, unlike CE/PE which are two).
    /// </summary>
    private void AddSpotEmaCross(List<OptionFlowSignal> signals, IndexFlowState state, long sessionStart, long sessionEnd)
    {
        if (state.SpotBars.Count == 0) return;

        var stamps = new List<DateTimeOffset>(state.SpotBars.Count);
        var closes = new List<decimal>(state.SpotBars.Count);

        foreach (var (minute, bar) in state.SpotBars)
        {
            if (minute < sessionStart || minute > sessionEnd) continue;
            stamps.Add(DateTimeOffset.FromUnixTimeSeconds(minute));
            closes.Add(bar.Close);
        }

        if (closes.Count == 0) return;

        var fast = OptionFlowMath.Ema(closes, _options.SignalFastEmaLength);
        var slow = OptionFlowMath.Ema(closes, _options.SignalSlowEmaLength);

        var signal = DetectCross("PriceEmaCross", "Price", "Index", stamps, fast, slow);
        if (signal is not null) signals.Add(signal);
    }

    private void AddEmaCross(List<OptionFlowSignal> signals, string metric, string scope,
        IReadOnlyList<DateTimeOffset> stamps, IReadOnlyList<decimal> raw)
    {
        if (raw.Count == 0) return;

        var fast = OptionFlowMath.Ema(raw, _options.SignalFastEmaLength);
        var slow = OptionFlowMath.Ema(raw, _options.SignalSlowEmaLength);

        var signal = DetectCross("EmaCross", metric, scope, stamps, fast, slow);
        if (signal is not null) signals.Add(signal);
    }

    private static void AddSeriesCross(List<OptionFlowSignal> signals, string metric,
        SideSeries call, SideSeries put, Func<SideSeries, IReadOnlyList<decimal>> select)
    {
        var callSeries = select(call);
        var putSeries = select(put);

        // The two sides can have different bar counts when one side's strikes have traded and the
        // other's haven't. Comparing them index-by-index would then align different minutes against
        // each other and invent crossings, so only the overlapping tail is compared.
        var count = Math.Min(callSeries.Count, putSeries.Count);
        if (count == 0) return;

        var stamps = call.Stamps.Skip(call.Stamps.Count - count).ToArray();
        var ce = callSeries.Skip(callSeries.Count - count).ToArray();
        var pe = putSeries.Skip(putSeries.Count - count).ToArray();

        var signal = DetectCross("CePeCross", metric, "CE/PE", stamps, ce, pe);
        if (signal is not null) signals.Add(signal);
    }

    /// <summary>
    /// Finds the most recent point at which <paramref name="fast"/> crossed <paramref name="slow"/>,
    /// and reports the CURRENT standing of the two either way. A series that has never crossed
    /// within the day still yields a signal with AtUtc null and BarsAgo -1, so the table can show
    /// "CE above PE, no cross today" rather than an empty row.
    /// </summary>
    private static OptionFlowSignal? DetectCross(string kind, string metric, string scope,
        IReadOnlyList<DateTimeOffset> stamps, IReadOnlyList<decimal> fast, IReadOnlyList<decimal> slow)
    {
        var count = Math.Min(Math.Min(fast.Count, slow.Count), stamps.Count);
        if (count == 0) return null;

        var last = count - 1;
        var bullish = fast[last] >= slow[last];

        for (var i = last; i > 0; i--)
        {
            var nowAbove = fast[i] >= slow[i];
            var thenAbove = fast[i - 1] >= slow[i - 1];
            if (nowAbove == thenAbove) continue;

            return new OptionFlowSignal(kind, metric, scope, nowAbove, stamps[i], last - i, fast[last], slow[last]);
        }

        return new OptionFlowSignal(kind, metric, scope, bullish, null, -1, fast[last], slow[last]);
    }

    private SideSeries BuildSide(IndexFlowState state, OptionRight right, long sessionStart, long sessionEnd)
    {
        var contracts = state.Window.Where(c => c.Right == right).ToArray();
        if (contracts.Length == 0) return SideSeries.Empty;

        // Every minute any tracked contract has a bar for, in order.
        var minutes = new SortedSet<long>();
        foreach (var contract in contracts)
        {
            if (!state.Bars.TryGetValue(contract.InstrumentToken, out var series)) continue;
            foreach (var minute in series.Keys)
                if (minute >= sessionStart && minute <= sessionEnd) minutes.Add(minute);
        }

        if (minutes.Count == 0) return SideSeries.Empty;

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

        return new SideSeries(points, stamps, cdvRaw, obvRaw);
    }

    /// <summary>One side's computed series: the smoothed points that get plotted, alongside the raw
    /// cumulative values the crossover signals are derived from - see BuildSignals.</summary>
    private sealed record SideSeries(
        IReadOnlyList<OptionFlowPoint> Points,
        IReadOnlyList<DateTimeOffset> Stamps,
        IReadOnlyList<decimal> RawCdv,
        IReadOnlyList<decimal> RawObv)
    {
        public static readonly SideSeries Empty = new(
            Array.Empty<OptionFlowPoint>(), Array.Empty<DateTimeOffset>(),
            Array.Empty<decimal>(), Array.Empty<decimal>());
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

        /// <summary>The index's own spot price, one minute bar at a time - see UpdateSpotLiveBar
        /// and AddSpotEmaCross. Kept separate from Bars (which is keyed by option contract token)
        /// since the spot line is a single series, not one per strike.</summary>
        public Dictionary<long, OptionBar> SpotBars { get; } = new();

        /// <summary>Tracks which of this index's alert-worthy signals have already fired, so the
        /// same crossing/threshold-breach never sends twice - see OptionsFlowAlertGate.</summary>
        public OptionsFlowAlertGate AlertGate { get; } = new();
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
