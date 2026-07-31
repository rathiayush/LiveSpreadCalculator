using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// On a fixed timer, recomputes every configured spread from the latest ticks in the store,
/// publishes only the spreads that actually changed since the last cycle, and evaluates trigger
/// rules against the fresh values inline. Never uses last traded price - LTP can be stale
/// between trades. Instead each leg is priced on whichever side you'd actually trade it: the
/// ask-ask difference decides direction using the classic calendar-spread arbitrage rule (near
/// month in premium -> sell near/buy far; near month at a discount -> buy near/sell far), then
/// the leg being bought is priced at its ask and the leg being sold at its bid - see the
/// direction comment inside RunCycleAsync for the full reasoning. This requires the ticker to
/// run in "full" mode (see KiteOptions.TickerMode) since only full-mode packets carry market
/// depth (both bid and ask). A single timer loop is simpler and cheap enough at ~100-200
/// instruments / ~100 spreads; it avoids extra channel plumbing between tick arrival and
/// trigger evaluation.
/// </summary>
public sealed class SpreadCalculationHostedService : BackgroundService
{
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly ITriggerRuleStore _ruleStore;
    private readonly IExitTriggerRuleStore _exitRuleStore;
    private readonly ITickStore _tickStore;
    private readonly ISpreadPublisher _publisher;
    private readonly ITriggerEventSink _triggerSink;
    private readonly TriggerEventChannel _triggerChannel;
    private readonly ISpreadSnapshotCache _snapshotCache;
    private readonly TriggerInFlightTracker _inFlightTracker;
    private readonly IPnlAlertNotifier _notifier;
    private readonly EngineOptions _options;
    private readonly ILogger<SpreadCalculationHostedService> _logger;

    private readonly Dictionary<string, decimal> _lastValues = new();

    // Daily spread-threshold alert state - see CheckDailySpreadAlertsAsync. Keyed by the
    // threshold value itself (not an index), so re-ordering/adding/removing tiers in config
    // between restarts never mixes up which tier "already fired today" refers to. Plain in-memory
    // state, not a CSV/database row - a restart losing "did tier X already fire today" costs at
    // most one duplicate message that day, which is a fine trade against not adding a new file to
    // manage for a once-a-day notification.
    private readonly TimeZoneInfo _ist;
    private readonly Dictionary<decimal, DateOnly> _tierLastAlertedIst = new();

    public SpreadCalculationHostedService(
        ISpreadDefinitionStore spreadStore,
        ITriggerRuleStore ruleStore,
        IExitTriggerRuleStore exitRuleStore,
        ITickStore tickStore,
        ISpreadPublisher publisher,
        ITriggerEventSink triggerSink,
        TriggerEventChannel triggerChannel,
        ISpreadSnapshotCache snapshotCache,
        TriggerInFlightTracker inFlightTracker,
        IPnlAlertNotifier notifier,
        IOptions<EngineOptions> options,
        ILogger<SpreadCalculationHostedService> logger)
    {
        _spreadStore = spreadStore;
        _ruleStore = ruleStore;
        _exitRuleStore = exitRuleStore;
        _tickStore = tickStore;
        _publisher = publisher;
        _triggerSink = triggerSink;
        _triggerChannel = triggerChannel;
        _snapshotCache = snapshotCache;
        _inFlightTracker = inFlightTracker;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
        _ist = ResolveIstTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, _options.SpreadRecalculationIntervalMs)));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spread recalculation cycle failed");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var changed = new List<SpreadSnapshot>();
        var currentIds = new HashSet<string>();
        // Fed alongside `changed` below but independent of it - the daily alert needs THIS
        // cycle's spread % for every spread that resolved a value, not just the ones whose value
        // happened to change since last cycle (a spread sitting motionless above a tier all day
        // must still be eligible to be the one that trips it the moment the tier itself opens up
        // for the day - e.g. right after DailySpreadAlertStartTimeIst).
        var spreadPercents = new List<(string Underlying, decimal SpreadPercent)>();

        foreach (var spread in _spreadStore.GetAll())
        {
            currentIds.Add(spread.Id);
            if (!spread.Enabled) continue;
            if (!_tickStore.TryGet(spread.NearLeg.InstrumentToken, out var nearTick)) continue;
            if (!_tickStore.TryGet(spread.FarLeg.InstrumentToken, out var farTick)) continue;
            // Both asks are needed just to decide direction (see below) - BestAskPrice is only
            // populated once a full-mode depth packet has arrived for that instrument, so until
            // then this spread is simply skipped rather than published with a wrong (zero) value.
            if (nearTick.BestAskPrice == 0 || farTick.BestAskPrice == 0) continue;

            // Decide direction from the ask-ask difference: this is the classic calendar-spread
            // arbitrage rule - sell whichever leg is trading rich, buy whichever leg is trading
            // cheap, so the position profits as the spread converges back toward fair value.
            //   - current (near) month in PREMIUM (near ask > far ask): SELL near, BUY far.
            //   - current (near) month in DISCOUNT (near ask < far ask): BUY near, SELL far.
            // Then price each leg on the side you'd actually trade it - ask for the leg you're
            // buying (what you'd pay), bid for the leg you're selling (what you'd receive) -
            // instead of comparing ask to ask, which overstates how good the spread really is
            // since it ignores the bid/ask cost of actually executing it. This Direction string is
            // purely informational now - it's carried on TriggerEvent/SpreadSnapshot for display
            // (the Dashboard grid and the Trigger Activity feed - see InMemoryTriggerActivityStore)
            // but is NOT consulted by OrderDispatchHostedService.BuildLegs to decide which leg to
            // buy/sell any more; that always comes straight from the rule's own fixed
            // NearLegAction/FarLegAction (the old "auto-direction" mode that used this string to
            // decide order direction live was removed - see TriggerRule's doc comments).
            var askAskDiff = farTick.BestAskPrice - nearTick.BestAskPrice;
            decimal nearPrice, farPrice;
            string direction;

            if (askAskDiff < 0)
            {
                // near is pricier (premium) -> sell near (receive bid), buy far (pay ask). The
                // near leg's bid tends to arrive later than its ask (fewer depth updates than a
                // heavily-quoted ask side) - rather than blacking the whole row out until it
                // shows up, fall back to that leg's ask so the row still shows a (slightly
                // optimistic) value in the meantime.
                nearPrice = nearTick.BestBidPrice != 0 ? nearTick.BestBidPrice : nearTick.BestAskPrice;
                farPrice = farTick.BestAskPrice;
                direction = nearTick.BestBidPrice != 0 ? "SellNear/BuyFar" : "SellNear/BuyFar (bid pending)";
            }
            else if (askAskDiff > 0)
            {
                // far is pricier, i.e. near is at a discount -> buy near (pay ask), sell far
                // (receive bid) - same fallback as above if the far leg's bid hasn't arrived yet.
                nearPrice = nearTick.BestAskPrice;
                farPrice = farTick.BestBidPrice != 0 ? farTick.BestBidPrice : farTick.BestAskPrice;
                direction = farTick.BestBidPrice != 0 ? "BuyNear/SellFar" : "BuyNear/SellFar (bid pending)";
            }
            else
            {
                // Exactly flat - no direction to take, fall back to ask/ask.
                nearPrice = nearTick.BestAskPrice;
                farPrice = farTick.BestAskPrice;
                direction = "Flat";
            }

            var spreadValue = farPrice - nearPrice;
            // Percentage relative to whichever near-leg price was actually used above - see
            // SpreadSnapshot.SpreadPercent.
            var spreadPercent = nearPrice == 0 ? 0m : spreadValue / nearPrice * 100m;

            // Both fixed directions' executable price, always both computed (unlike SpreadValue/
            // Direction above, which only reflects whichever direction the ask-ask rule currently
            // favors) - see SpreadSnapshot.BuySpreadValue/SellSpreadValue. Same bid-missing-yet
            // fallback to that leg's own ask used everywhere else in this cycle.
            var nearBidOrAsk = nearTick.BestBidPrice != 0 ? nearTick.BestBidPrice : nearTick.BestAskPrice;
            var farBidOrAsk = farTick.BestBidPrice != 0 ? farTick.BestBidPrice : farTick.BestAskPrice;
            var buySpreadValue = farBidOrAsk - nearTick.BestAskPrice; // BuyNear(ask) / SellFar(bid)
            var sellSpreadValue = farTick.BestAskPrice - nearBidOrAsk; // SellNear(bid) / BuyFar(ask)
            // Percentage counterparts, each relative to the near-leg price actually paid/received
            // in that fixed direction - see SpreadSnapshot.BuySpreadPercent/SellSpreadPercent and
            // EvaluateTriggersAsync/ResolveMeasuredValue, which pick whichever of these four
            // (raw/percent x buy/sell) matches a given rule's own fixed leg actions.
            var buySpreadPercent = nearTick.BestAskPrice == 0 ? 0m : buySpreadValue / nearTick.BestAskPrice * 100m;
            var sellSpreadPercent = nearBidOrAsk == 0 ? 0m : sellSpreadValue / nearBidOrAsk * 100m;

            var snapshot = new SpreadSnapshot(
                spread.Id, spread.Underlying, nearPrice, farPrice, direction, spreadValue, spreadPercent, DateTimeOffset.UtcNow,
                buySpreadValue, sellSpreadValue, buySpreadPercent, sellSpreadPercent);

            // Always kept fresh for the ranking job / GET /api/spreads/snapshot, independent of
            // whether the value actually changed since the last cycle.
            _snapshotCache.Upsert(snapshot);
            spreadPercents.Add((spread.Underlying, spreadPercent));

            // This "did it change" check only gates the SignalR push below - it must NOT gate
            // trigger evaluation. Previously it did both (a `continue` skipped evaluation
            // entirely when the value was unchanged), which meant that once the market closes
            // and ticks stop moving, spreadValue is bit-for-bit identical cycle after cycle -
            // so EvaluateTriggersAsync would never run again for that spread AT ALL, for any
            // rule, new or old, until a tick actually moved the value. A rule added (or
            // re-armed) while the market is frozen would sit there Enabled/IsArmed=true forever
            // and never get evaluated against the current snapshot - indistinguishable from
            // being broken. Evaluating every cycle is cheap (in-memory only, no I/O unless a
            // rule actually fires) - only the dashboard push benefits from being skipped when
            // nothing changed.
            var isChanged = !_lastValues.TryGetValue(spread.Id, out var last) || last != spreadValue;
            _lastValues[spread.Id] = spreadValue;
            if (isChanged) changed.Add(snapshot);

            // Entry and exit rules live in separate stores (see IExitTriggerRuleStore) and are
            // evaluated as two independent passes so neither can ever be silently skipped or
            // double-evaluated - isExitTrigger just gets stamped onto whatever TriggerEvent comes
            // out of each pass (see TriggerEvent.IsExitTrigger) for the Trigger Activity feed.
            await EvaluateTriggersAsync(spread, snapshot, _ruleStore, isExitTrigger: false, stoppingToken);
            await EvaluateTriggersAsync(spread, snapshot, _exitRuleStore, isExitTrigger: true, stoppingToken);
        }

        if (changed.Count > 0)
            await _publisher.PublishAsync(changed, stoppingToken);

        // Self-prune: _lastValues has no other removal path, so a spread deleted via
        // DELETE /api/spreads (see SpreadsController) would otherwise sit here forever. Cheap
        // to check every cycle since it only touches the small "removed since last cycle" set,
        // not the whole dictionary on every pass.
        if (_lastValues.Count > currentIds.Count)
        {
            foreach (var staleId in _lastValues.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                _lastValues.Remove(staleId);
        }

        await CheckDailySpreadAlertsAsync(spreadPercents, stoppingToken);
    }

    /// <summary>
    /// Once per configured tier per IST calendar day (see EngineOptions.DailySpreadAlertThresholdsPercent),
    /// sends one Telegram message listing every spread whose |spread %| is currently at or past that
    /// tier, each with its own SIGNED spread % (not the absolute value, so the message shows which way
    /// each spread has moved). Runs inline at the end of every recalculation cycle rather than as its
    /// own hosted service/timer or CSV-backed store - this already has, for free, exactly the fresh
    /// spread % for every spread that a separate poller would have to fetch or recompute itself.
    ///
    /// Silent before EngineOptions.DailySpreadAlertStartTimeIst (spread % right at the open can be
    /// noisy before real liquidity shows up) and silent again for the rest of the day once a given
    /// tier has already fired - tracked by calendar date, not a timer, so this is naturally correct
    /// across restarts (a restart mid-day does NOT re-send a tier that already fired earlier today
    /// unless the in-memory _tierLastAlertedIst was itself lost by that same restart - see its field
    /// comment for why that's an accepted trade-off rather than persisted).
    /// </summary>
    private async Task CheckDailySpreadAlertsAsync(
        List<(string Underlying, decimal SpreadPercent)> spreadPercents, CancellationToken cancellationToken)
    {
        if (!_options.DailySpreadAlertEnabled || _options.DailySpreadAlertThresholdsPercent.Count == 0)
            return;

        var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);
        if (!TimeSpan.TryParse(_options.DailySpreadAlertStartTimeIst, out var startTime) || nowIst.TimeOfDay < startTime)
            return;

        var today = DateOnly.FromDateTime(nowIst.DateTime);

        foreach (var threshold in _options.DailySpreadAlertThresholdsPercent)
        {
            if (_tierLastAlertedIst.TryGetValue(threshold, out var lastAlerted) && lastAlerted == today)
                continue; // already sent this tier's alert today

            var crossers = spreadPercents
                .Where(s => Math.Abs(s.SpreadPercent) >= threshold)
                .OrderByDescending(s => Math.Abs(s.SpreadPercent))
                .ToList();
            if (crossers.Count == 0) continue;

            var lines = crossers.Select(s => $"{s.Underlying}: {(s.SpreadPercent >= 0 ? "+" : "")}{s.SpreadPercent:0.00}%");
            var text = $"⚠️ <b>Daily spread alert - {threshold:0.##}% tier</b>\n" +
                       $"{crossers.Count} spread(s) at or past ±{threshold:0.##}% today:\n\n" +
                       string.Join("\n", lines);

            await _notifier.SendAsync(text, cancellationToken);
            _tierLastAlertedIst[threshold] = today;

            _logger.LogInformation(
                "Daily spread alert sent for the {Threshold}% tier - {Count} spread(s): {Underlyings}",
                threshold, crossers.Count, string.Join(", ", crossers.Select(c => c.Underlying)));
        }
    }

    private static TimeZoneInfo ResolveIstTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // IST is a fixed UTC+05:30 with no DST, so a hand-built zone is exact.
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "IST");
    }

    private async Task EvaluateTriggersAsync(SpreadDefinition spread, SpreadSnapshot snapshot, ITriggerRuleStore ruleStore, bool isExitTrigger, CancellationToken stoppingToken)
    {
        foreach (var rule in ruleStore.GetForSpread(spread.Id))
        {
            if (!rule.Enabled) continue;

            var measured = ResolveMeasuredValue(rule, snapshot);

            // AbsoluteGreaterThanOrEqual (WatchBothSides) judges |measured| against a positive-
            // only Threshold (see TriggersController.Create) - it doesn't matter which side of
            // zero the spread is on, only how far it's deviated from it either way.
            var crossed = rule.Operator switch
            {
                TriggerOperator.GreaterThanOrEqual => measured >= rule.Threshold,
                TriggerOperator.LessThanOrEqual => measured <= rule.Threshold,
                TriggerOperator.AbsoluteGreaterThanOrEqual => Math.Abs(measured) >= rule.Threshold,
                _ => false,
            };

            // ---- Bounded "N sequential shots" mode (TriggerRule.MaxFireCount > 0) -------------
            // Deliberately bypasses IsArmed entirely: the point of this mode is that
            // each of the N scheduled shots fires as soon as the condition is true again AND the
            // previous shot's basket has actually resolved (filled or rejected, via
            // TriggerInFlightTracker) - not after the spread has retreated past some buffer first.
            // If the condition happens to be false on a given cycle, that check is just skipped -
            // FireCount isn't touched, so it resumes from wherever it left off the next time the
            // condition is true again, rather than losing progress or needing to re-arm.
            if (!rule.OneShot && rule.MaxFireCount > 0)
            {
                if (rule.FireCount >= rule.MaxFireCount) continue; // guards a stale/hand-edited row; Enabled is normally cleared on the last shot
                if (!crossed) continue;
                if (_inFlightTracker.IsInFlight(rule.Id)) continue; // previous shot's order outcome isn't known yet
                if (rule.LastFiredAt is { } lastSeqFired &&
                    DateTimeOffset.UtcNow - lastSeqFired < TimeSpan.FromMilliseconds(_options.MinFireIntervalPerRuleMs))
                    continue;

                rule.FireCount++;
                rule.LastFiredAt = DateTimeOffset.UtcNow;
                if (rule.FireCount >= rule.MaxFireCount) rule.Enabled = false;
                ruleStore.Upsert(rule); // persist shot count/fired/disabled state so a restart doesn't lose or repeat a shot

                var seqTriggerEvent = new TriggerEvent(Guid.NewGuid(), rule.Id, spread.Id, measured, snapshot.Direction, DateTimeOffset.UtcNow, isExitTrigger);
                _logger.LogInformation("Trigger fired (shot {FireCount}/{MaxFireCount}): rule={RuleId} spread={SpreadId} value={Value} direction={Direction}",
                    rule.FireCount, rule.MaxFireCount, rule.Id, spread.Id, measured, snapshot.Direction);

                await _triggerSink.PublishAsync(seqTriggerEvent, stoppingToken);
                await _triggerChannel.Writer.WriteAsync(seqTriggerEvent, stoppingToken);
                SendTriggerMetNotification(rule, spread, measured, isExitTrigger, snapshot.Direction);
                continue;
            }

            // ---- Standard one-shot mode (also covers a bare OneShot=false/MaxFireCount=0 rule,
            // which just fires once and then sits dormant until manually re-armed - see
            // TriggersController.Enable) -----------------------------------------------------
            // No re-arm/hysteresis logic any more: the old "stand indefinitely, re-arm once the
            // spread retreats past a buffer" mode was removed. IsArmed is now purely "has this
            // fired since it was last (re-)enabled" - once false, only TriggersController.Enable
            // (which resets it to true) can make this rule eligible to fire again.
            if (!crossed) continue;
            if (!rule.IsArmed) continue;
            if (rule.LastFiredAt is { } lastFired &&
                DateTimeOffset.UtcNow - lastFired < TimeSpan.FromMilliseconds(_options.MinFireIntervalPerRuleMs))
                continue;

            rule.LastFiredAt = DateTimeOffset.UtcNow;
            rule.IsArmed = false;
            if (rule.OneShot) rule.Enabled = false;
            ruleStore.Upsert(rule); // persist fired/disabled state so a restart doesn't re-fire it

            var triggerEvent = new TriggerEvent(Guid.NewGuid(), rule.Id, spread.Id, measured, snapshot.Direction, DateTimeOffset.UtcNow, isExitTrigger);
            _logger.LogInformation("Trigger fired: rule={RuleId} spread={SpreadId} value={Value} direction={Direction}",
                rule.Id, spread.Id, measured, snapshot.Direction);

            await _triggerSink.PublishAsync(triggerEvent, stoppingToken);
            await _triggerChannel.Writer.WriteAsync(triggerEvent, stoppingToken);
            SendTriggerMetNotification(rule, spread, measured, isExitTrigger, snapshot.Direction);
        }
    }

    /// <summary>
    /// Tells the user the instant a trigger's condition is satisfied - separate from (and earlier
    /// than) TelegramOrderExecutionSink's message, which only fires once OrderDispatchHostedService
    /// has finished acting on the TriggerEvent this same cycle just queued (margin check, order
    /// placement, fill/reject outcome). This one answers "did my trigger price get hit at all",
    /// independent of whatever the resulting order actually does. Fire-and-forget with
    /// CancellationToken.None, same as TelegramOrderExecutionSink - a slow/hung Telegram call must
    /// never delay evaluating the rest of this cycle's rules or the order dispatch that already
    /// happened above via the channel write, and TelegramNotifier.SendAsync itself never throws
    /// (best-effort, logs and swallows), so there is nothing here to catch.
    /// </summary>
    private void SendTriggerMetNotification(TriggerRule rule, SpreadDefinition spread, decimal measured, bool isExitTrigger, string direction) =>
        _ = _notifier.SendAsync(BuildTriggerMetMessage(rule, spread, measured, isExitTrigger, direction), CancellationToken.None);

    private string BuildTriggerMetMessage(TriggerRule rule, SpreadDefinition spread, decimal measured, bool isExitTrigger, string direction)
    {
        var kind = isExitTrigger ? "Exit" : "Entry";
        var unit = rule.ThresholdIsPercent ? "%" : "";
        var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _ist);

        return $"🎯 <b>{kind} trigger price met</b>\n" +
               $"Underlying: {spread.Underlying}\n" +
               $"Condition: {DescribeCondition(rule)}\n" +
               $"Value at trigger: {measured:0.00}{unit} ({direction})\n" +
               $"Order is being placed now.\n" +
               $"{nowIst:dd-MMM HH:mm:ss} IST";
    }

    private static string DescribeCondition(TriggerRule rule)
    {
        var unit = rule.ThresholdIsPercent ? "%" : "";
        return rule.Operator switch
        {
            TriggerOperator.GreaterThanOrEqual => $"rises to/above {rule.Threshold:0.##}{unit}",
            TriggerOperator.LessThanOrEqual => $"falls to/below {rule.Threshold:0.##}{unit}",
            TriggerOperator.AbsoluteGreaterThanOrEqual => $"moves ±{rule.Threshold:0.##}{unit} from zero",
            _ => $"{rule.Operator} {rule.Threshold:0.##}{unit}",
        };
    }

    /// <summary>Picks which of the snapshot's four measured values (raw/percent x auto-direction/
    /// fixed-direction) a given rule's Threshold should actually be compared against. A rule with
    /// FIXED Buy-near/Sell-far or Sell-near/Buy-far leg actions (every rule created through the
    /// current UI - see TriggerForm.jsx/AddPositionExitTriggerPage.jsx) is priced off the matching
    /// BuySpreadValue/SellSpreadValue(/Percent), which are ALWAYS computed for both directions
    /// regardless of which one the ask-ask auto-direction rule currently favors - critical for an
    /// exit trigger, whose required closing direction is fixed by the position it's closing and
    /// has no reason to line up with whichever side happens to be richer/cheaper this tick. Any
    /// other leg-action combination (single-leg, both-Buy, both-Sell, or a legacy
    /// AbsoluteGreaterThanOrEqual/watch-both-sides row) has no well-defined fixed direction to
    /// price off, so it falls back to the original auto-direction SpreadValue/SpreadPercent.</summary>
    private static decimal ResolveMeasuredValue(TriggerRule rule, SpreadSnapshot snapshot)
    {
        if (rule.NearLegAction == TriggerLegAction.Buy && rule.FarLegAction == TriggerLegAction.Sell)
            return rule.ThresholdIsPercent ? snapshot.BuySpreadPercent : snapshot.BuySpreadValue;
        if (rule.NearLegAction == TriggerLegAction.Sell && rule.FarLegAction == TriggerLegAction.Buy)
            return rule.ThresholdIsPercent ? snapshot.SellSpreadPercent : snapshot.SellSpreadValue;
        return rule.ThresholdIsPercent ? snapshot.SpreadPercent : snapshot.SpreadValue;
    }
}
