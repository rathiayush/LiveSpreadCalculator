using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// In-memory rig for driving a real OrderDispatchHostedService end to end - a genuine TriggerEvent
/// in, the resulting BasketExecutionEvent and the full record of what each broker call received out.
///
/// Everything below the service is faked (order gateway, margin, stores, ticks) but the service
/// itself is the production class, unmodified: the point is to pin the ORCHESTRATION - hedge-first
/// sequencing, margin gating, retry/always-fill, rule re-arming - which is where the money is and
/// where none of it was previously covered by a test.
/// </summary>
internal sealed class DispatchHarness
{
    public FakeOrderGateway Gateway { get; } = new();
    public FakeMarginService Margin { get; } = new();
    public FakeExecutionSink Sink { get; } = new();
    public InMemoryRuleStore Rules { get; } = new();
    public InMemoryRuleStore ExitRules { get; } = new();
    public InMemorySpreadStore Spreads { get; } = new();
    public FakeTickStore Ticks { get; } = new();
    public EngineOptions Options { get; } = new()
    {
        VerifyMarginBeforeDispatch = false, // opted into per-test, so most tests aren't writing margin fixtures
        AutoSquareOffOnPartialFill = false, // production default: always-fill
        RetryUnfilledLegOnPartialFill = true,
        RetryUnfilledLegMaxAttempts = 4,
        RetryUnfilledLegSlippageMultiplier = 3m,
        MaxUnfilledDispatchAttempts = 10,
    };

    public const string RuleId = "rule-1";
    public const string SpreadId = "TEST-AUG-SEP";
    public const uint NearToken = 101;
    public const uint FarToken = 202;

    /// <summary>A standard 2-leg calendar spread rule: BUY the near leg, SELL the far leg, with
    /// live bid/ask on both so market protection prices them normally.</summary>
    public TriggerRule SeedCalendarSpread(int quantity = 2, bool oneShot = true, int maxFireCount = 0)
    {
        Spreads.Upsert(new SpreadDefinition
        {
            Id = SpreadId,
            Underlying = "TEST",
            NearLeg = new InstrumentConfig { TradingSymbol = "TEST26AUGFUT", Exchange = "NFO", InstrumentToken = NearToken, TickSize = 0.05m, LotSize = 1 },
            FarLeg = new InstrumentConfig { TradingSymbol = "TEST26SEPFUT", Exchange = "NFO", InstrumentToken = FarToken, TickSize = 0.05m, LotSize = 1 },
        });

        foreach (var token in new[] { NearToken, FarToken })
            Ticks.Update(new Tick(token, 100m, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BestBidPrice: 99m, BestAskPrice: 101m));

        var rule = new TriggerRule
        {
            Id = RuleId,
            SpreadId = SpreadId,
            Operator = TriggerOperator.LessThanOrEqual,
            Threshold = -20m,
            NearLegAction = TriggerLegAction.Buy,
            FarLegAction = TriggerLegAction.Sell,
            Quantity = quantity,
            OrderType = "MARKET",
            SlippagePercent = 0.10m,
            OneShot = oneShot,
            MaxFireCount = maxFireCount,
            IsArmed = false,   // as EvaluateTriggersAsync leaves it once it has fired
            Enabled = !oneShot,
            FireCount = maxFireCount > 0 ? 1 : 0,
        };
        Rules.Upsert(rule);
        return rule;
    }

    /// <summary>Runs one real dispatch to completion and returns the published outcome.</summary>
    public async Task<BasketExecutionEvent> DispatchAsync(bool isExitTrigger = false)
    {
        var channel = new TriggerEventChannel();
        var service = new OrderDispatchHostedService(
            channel,
            Rules,
            ExitRules,
            Spreads,
            Gateway,
            new BasketMarginGate(Margin, Microsoft.Extensions.Options.Options.Create(Options), new McxOptions(), NullLogger<BasketMarginGate>.Instance),
            Sink,
            new MarketProtectionService(Ticks, NullLogger<MarketProtectionService>.Instance),
            new TriggerInFlightTracker(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<OrderDispatchHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await channel.Writer.WriteAsync(
            new TriggerEvent(Guid.NewGuid(), RuleId, SpreadId, -21m, "BuyNear/SellFar", DateTimeOffset.UtcNow, isExitTrigger),
            cts.Token);

        var published = await Sink.WaitForNextAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);
        return published;
    }
}

internal sealed class PlacedOrder
{
    public required string TradingSymbol { get; init; }
    public required string TransactionType { get; init; }
    public required int Quantity { get; init; }
    public decimal? Price { get; init; }
}

/// <summary>Records every order it is asked to place and answers from a queued script, so a test
/// can say "this leg fills 1 of 2, then the retry fills the rest" and assert on exactly what the
/// service sent and in what order.</summary>
internal sealed class FakeOrderGateway : IOrderGateway
{
    private readonly Dictionary<string, Queue<OrderResult>> _scripted = new();

    public List<PlacedOrder> Placed { get; } = new();
    public List<string> Cancelled { get; } = new();

    /// <summary>Default outcome for any symbol/side with no script left - a clean full fill.</summary>
    public Func<OrderLegRequest, OrderResult> Default { get; set; } =
        leg => new OrderResult { Success = true, OrderId = "auto", FilledQuantity = leg.Quantity, Status = "COMPLETE", AverageFillPrice = leg.Price };

    public void Script(string tradingSymbol, params OrderResult[] results)
    {
        var queue = _scripted.TryGetValue(tradingSymbol, out var existing) ? existing : _scripted[tradingSymbol] = new Queue<OrderResult>();
        foreach (var r in results) queue.Enqueue(r);
    }

    public Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken)
    {
        Placed.Add(new PlacedOrder
        {
            TradingSymbol = leg.TradingSymbol,
            TransactionType = leg.TransactionType,
            Quantity = leg.Quantity,
            Price = leg.Price,
        });

        if (_scripted.TryGetValue(leg.TradingSymbol, out var queue) && queue.Count > 0)
            return Task.FromResult(queue.Dequeue());

        return Task.FromResult(Default(leg));
    }

    public Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken)
    {
        Cancelled.Add(orderId);
        return Task.FromResult(true);
    }
}

internal sealed class FakeMarginService : IMarginService
{
    public decimal? BasketRequired { get; set; } = 1000m;
    public decimal? Available { get; set; } = 1_000_000m;
    public bool BasketCheckSucceeds { get; set; } = true;
    public bool AvailableCheckSucceeds { get; set; } = true;

    public Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken) =>
        Task.FromResult(new BasketMarginResult(BasketCheckSucceeds, BasketRequired, BasketRequired, BasketCheckSucceeds ? null : "margin check unavailable"));

    public Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default) =>
        Task.FromResult(new AvailableMarginResult(AvailableCheckSucceeds, Available, AvailableCheckSucceeds ? null : "limits unavailable"));
}

internal sealed class FakeExecutionSink : IOrderExecutionSink
{
    private readonly TaskCompletionSource<BasketExecutionEvent> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        _next.TrySetResult(executionEvent);
        return Task.CompletedTask;
    }

    public async Task<BasketExecutionEvent> WaitForNextAsync(CancellationToken cancellationToken) =>
        await _next.Task.WaitAsync(cancellationToken);
}

internal sealed class InMemoryRuleStore : ITriggerRuleStore, IExitTriggerRuleStore
{
    private readonly Dictionary<string, TriggerRule> _rules = new();

    public void Upsert(TriggerRule rule) => _rules[rule.Id] = rule;
    public bool Remove(string ruleId) => _rules.Remove(ruleId);
    public int RemoveAll() { var n = _rules.Count; _rules.Clear(); return n; }
    public IReadOnlyList<TriggerRule> GetAll() => _rules.Values.ToArray();
    public IReadOnlyList<TriggerRule> GetForSpread(string spreadId) => _rules.Values.Where(r => r.SpreadId == spreadId).ToArray();
    public TriggerRule? Get(string ruleId) => _rules.TryGetValue(ruleId, out var r) ? r : null;
}

internal sealed class InMemorySpreadStore : ISpreadDefinitionStore
{
    private readonly Dictionary<string, SpreadDefinition> _spreads = new();

    public void Upsert(SpreadDefinition definition) => _spreads[definition.Id] = definition;
    public bool Remove(string spreadId) => _spreads.Remove(spreadId);
    public IReadOnlyList<SpreadDefinition> GetAll() => _spreads.Values.ToArray();
    public SpreadDefinition? Get(string spreadId) => _spreads.TryGetValue(spreadId, out var s) ? s : null;
}

internal sealed class FakeTickStore : ITickStore
{
    private readonly Dictionary<uint, Tick> _ticks = new();

    public void Update(Tick tick) => _ticks[tick.InstrumentToken] = tick;
    public bool TryGet(uint instrumentToken, out Tick tick) => _ticks.TryGetValue(instrumentToken, out tick);
    public IReadOnlyCollection<Tick> Snapshot() => _ticks.Values.ToArray();
    public void Remove(uint instrumentToken) => _ticks.Remove(instrumentToken);
}
