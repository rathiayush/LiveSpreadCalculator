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
    private readonly ITickStore _tickStore;
    private readonly ISpreadPublisher _publisher;
    private readonly ITriggerEventSink _triggerSink;
    private readonly TriggerEventChannel _triggerChannel;
    private readonly ISpreadSnapshotCache _snapshotCache;
    private readonly EngineOptions _options;
    private readonly ILogger<SpreadCalculationHostedService> _logger;

    private readonly Dictionary<string, decimal> _lastValues = new();

    public SpreadCalculationHostedService(
        ISpreadDefinitionStore spreadStore,
        ITriggerRuleStore ruleStore,
        ITickStore tickStore,
        ISpreadPublisher publisher,
        ITriggerEventSink triggerSink,
        TriggerEventChannel triggerChannel,
        ISpreadSnapshotCache snapshotCache,
        IOptions<EngineOptions> options,
        ILogger<SpreadCalculationHostedService> logger)
    {
        _spreadStore = spreadStore;
        _ruleStore = ruleStore;
        _tickStore = tickStore;
        _publisher = publisher;
        _triggerSink = triggerSink;
        _triggerChannel = triggerChannel;
        _snapshotCache = snapshotCache;
        _options = options.Value;
        _logger = logger;
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

        foreach (var spread in _spreadStore.GetAll())
        {
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
            // since it ignores the bid/ask cost of actually executing it. This exact Direction
            // string is carried through to OrderDispatchHostedService via TriggerEvent.Direction
            // rather than re-derived from SpreadValue's sign there, since pricing the sell leg at
            // its (lower) bid can occasionally narrow or even flip the sign of SpreadValue versus
            // the ask-ask call that decided direction - the direction decided here is always the
            // one that's actually used, with no risk of the two disagreeing.
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

            var snapshot = new SpreadSnapshot(
                spread.Id, spread.Underlying, nearPrice, farPrice, direction, spreadValue, spreadPercent, DateTimeOffset.UtcNow);

            // Always kept fresh for the ranking job / GET /api/spreads/snapshot, independent of
            // whether the value actually changed since the last cycle.
            _snapshotCache.Upsert(snapshot);

            if (_lastValues.TryGetValue(spread.Id, out var last) && last == spreadValue)
                continue;

            _lastValues[spread.Id] = spreadValue;
            changed.Add(snapshot);

            await EvaluateTriggersAsync(spread, snapshot, stoppingToken);
        }

        if (changed.Count > 0)
            await _publisher.PublishAsync(changed, stoppingToken);
    }

    private async Task EvaluateTriggersAsync(SpreadDefinition spread, SpreadSnapshot snapshot, CancellationToken stoppingToken)
    {
        foreach (var rule in _ruleStore.GetForSpread(spread.Id))
        {
            if (!rule.Enabled) continue;

            // ThresholdIsPercent rules judge SpreadPercent (comparable across stocks at very
            // different price levels); legacy rules judge the raw ask-price SpreadValue.
            var measured = rule.ThresholdIsPercent ? snapshot.SpreadPercent : snapshot.SpreadValue;

            var crossed = rule.Operator == TriggerOperator.GreaterThanOrEqual
                ? measured >= rule.Threshold
                : measured <= rule.Threshold;

            if (!crossed)
            {
                if (!rule.IsArmed)
                {
                    var rearmed = rule.Operator == TriggerOperator.GreaterThanOrEqual
                        ? measured <= rule.Threshold - rule.RearmBuffer
                        : measured >= rule.Threshold + rule.RearmBuffer;
                    if (rearmed)
                    {
                        rule.IsArmed = true;
                        _ruleStore.Upsert(rule); // persist the re-arm (CSV store rewrites the file)
                    }
                }
                continue;
            }

            if (!rule.IsArmed) continue;
            if (rule.LastFiredAt is { } lastFired &&
                DateTimeOffset.UtcNow - lastFired < TimeSpan.FromMilliseconds(_options.MinFireIntervalPerRuleMs))
                continue;

            rule.LastFiredAt = DateTimeOffset.UtcNow;
            rule.IsArmed = false;
            if (rule.OneShot) rule.Enabled = false;
            _ruleStore.Upsert(rule); // persist fired/disabled state so a restart doesn't re-fire it

            var triggerEvent = new TriggerEvent(rule.Id, spread.Id, snapshot.SpreadValue, snapshot.Direction, DateTimeOffset.UtcNow);
            _logger.LogInformation("Trigger fired: rule={RuleId} spread={SpreadId} value={Value} direction={Direction}",
                rule.Id, spread.Id, snapshot.SpreadValue, snapshot.Direction);

            await _triggerSink.PublishAsync(triggerEvent, stoppingToken);
            await _triggerChannel.Writer.WriteAsync(triggerEvent, stoppingToken);
        }
    }
}
