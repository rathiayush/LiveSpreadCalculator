using Microsoft.Extensions.Logging.Abstractions;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Store;
using SpreadTrader.Engine;
using SpreadTrader.Engine.Services;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Regression tests for the gap that shipped with the spot/basis feature: spot resolution was wired
/// into ResolveAndSubscribeAsync, the path taken when a spread is added ONE AT A TIME through the
/// API - but not into UniverseBuilderService, which is how essentially every spread is actually
/// created. The builder creates spreads in bulk and subscribes their tokens in a single batch,
/// bypassing that path entirely, so the whole live universe had no spot leg and every Spot and
/// Basis % cell on the grid was blank.
///
/// These drive the REAL UniverseBuilderService end to end against a fake instrument master, so the
/// assertion is about what the builder genuinely produces rather than about a helper it is merely
/// expected to call.
/// </summary>
public class UniverseSpotWiringTests
{
    private const uint NearToken = 1001;
    private const uint FarToken = 1002;
    private const uint SpotToken = 2001;

    private static (UniverseBuilderService Builder, InMemorySpreadStore Spreads, RecordingFeed Feed) BuildRig(
        bool spotAvailable = true)
    {
        var master = new FakeSpotAwareMaster(spotAvailable);
        var spreads = new InMemorySpreadStore();
        var feed = new RecordingFeed();

        var positions = new PositionsCache(
            new NoPositionsService(), feed, new FakeTickStore(), master, NullLogger<PositionsCache>.Instance);

        var ingestion = new TickIngestionHostedService(
            spreads, master, feed, new FakeTickStore(), positions, new CdvTokenRegistry(), NullLogger<TickIngestionHostedService>.Instance);

        var builder = new UniverseBuilderService(
            master, spreads, ingestion, NullLogger<UniverseBuilderService>.Instance,
            outputCsvPath: Path.Combine(Path.GetTempPath(), $"universe-{Guid.NewGuid():N}.csv"),
            mcx: new McxOptions(),
            mcxOutputCsvPath: Path.Combine(Path.GetTempPath(), $"universe-mcx-{Guid.NewGuid():N}.csv"));

        return (builder, spreads, feed);
    }

    [Fact]
    public async Task AUniverseBuiltSpreadGetsItsSpotLegResolved()
    {
        // THE regression. Before the fix every spread the universe builder created had SpotLeg
        // null, so the grid showed a dash for every underlying however healthy the feed was.
        var (builder, spreads, _) = BuildRig();

        await builder.BuildAsync(maxSymbols: null, CancellationToken.None);

        var spread = Assert.Single(spreads.GetAll());
        Assert.NotNull(spread.SpotLeg);
        Assert.Equal(SpotToken, spread.SpotLeg!.InstrumentToken);
    }

    [Fact]
    public async Task TheSpotTokenIsActuallySubscribedWithTheLegs()
    {
        // Resolving the leg is only half of it - if the token never reaches the feed's subscribe
        // batch, no spot tick ever arrives and the column stays blank anyway.
        var (builder, _, feed) = BuildRig();

        await builder.BuildAsync(maxSymbols: null, CancellationToken.None);

        Assert.Contains(NearToken, feed.Subscribed);
        Assert.Contains(FarToken, feed.Subscribed);
        Assert.Contains(SpotToken, feed.Subscribed);
    }

    [Fact]
    public async Task AnUnderlyingWithNoSpotStillBuildsNormally()
    {
        // MCX commodities and anything without a cash listing must be unaffected: no spot leg, no
        // extra subscription, and above all no failure - the spread itself still works.
        var (builder, spreads, feed) = BuildRig(spotAvailable: false);

        await builder.BuildAsync(maxSymbols: null, CancellationToken.None);

        var spread = Assert.Single(spreads.GetAll());
        Assert.Null(spread.SpotLeg);
        Assert.Contains(NearToken, feed.Subscribed);
        Assert.Contains(FarToken, feed.Subscribed);
        Assert.DoesNotContain(SpotToken, feed.Subscribed);
    }

    // ---- Fakes --------------------------------------------------------------------------------

    /// <summary>One underlying, two expiries, and (optionally) a cash instrument for it.</summary>
    private sealed class FakeSpotAwareMaster : IBrokerInstrumentMaster
    {
        private readonly bool _spotAvailable;

        private static readonly BrokerInstrument Near =
            new(NearToken, "TEST26AUGFUT", "TEST", "NFO", "NFO-FUT", "FUT", new DateOnly(2026, 8, 27), 100, 0.05m);
        private static readonly BrokerInstrument Far =
            new(FarToken, "TEST26SEPFUT", "TEST", "NFO", "NFO-FUT", "FUT", new DateOnly(2026, 9, 24), 100, 0.05m);
        private static readonly BrokerInstrument Spot =
            new(SpotToken, "TEST", "TEST LIMITED", "NSE", "NSE", "EQ", null, 0, 0.05m);

        public FakeSpotAwareMaster(bool spotAvailable) => _spotAvailable = spotAvailable;

        public Task EnsureLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public BrokerInstrument? Find(string tradingSymbol, string exchange) =>
            new[] { Near, Far }.FirstOrDefault(i => i.TradingSymbol == tradingSymbol);

        public IReadOnlyList<BrokerInstrument> FindFutures(string underlyingName, string exchange = "NFO") =>
            string.Equals(underlyingName, "TEST", StringComparison.OrdinalIgnoreCase)
                ? new[] { Near, Far }
                : Array.Empty<BrokerInstrument>();

        public IReadOnlyList<BrokerInstrument> GetAllFutures(string? exchange = null) => new[] { Near, Far };

        public IReadOnlyList<string> GetAllFuturesUnderlyings(string exchange = "NFO") =>
            string.Equals(exchange, "NFO", StringComparison.OrdinalIgnoreCase) ? new[] { "TEST" } : Array.Empty<string>();

        public Task<IReadOnlyList<BrokerInstrument>> FetchAndSaveFuturesAsync(string? exchange, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrokerInstrument>>(new[] { Near, Far });

        public Task ResolveAsync(InstrumentConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public BrokerInstrument? FindSpot(string underlyingName) =>
            _spotAvailable && string.Equals(underlyingName, "TEST", StringComparison.OrdinalIgnoreCase) ? Spot : null;

        public IReadOnlyList<BrokerInstrument> GetAllSpot() =>
            _spotAvailable ? new[] { Spot } : Array.Empty<BrokerInstrument>();
    }

    /// <summary>Records every token the builder asks to be subscribed.</summary>
    private sealed class RecordingFeed : IMarketDataFeed
    {
        public HashSet<uint> Subscribed { get; } = new();

        public event Action<Tick>? TickReceived;
        public event Action? OrderUpdateReceived;

        public Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
        {
            foreach (var token in instrumentTokens) Subscribed.Add(token);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
        {
            foreach (var token in instrumentTokens) Subscribed.Remove(token);
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<uint> GetSubscribedTokens() => Subscribed.ToArray();

        public Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _ = TickReceived;
            _ = OrderUpdateReceived;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoPositionsService : IPositionsService
    {
        public Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PositionsResult(true, Array.Empty<PositionSnapshot>(), null));
    }
}
