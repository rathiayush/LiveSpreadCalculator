using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Ticker;
using SpreadTrader.KiteIntegration.Ticker;
using SpreadTrader.KotakIntegration.Ticker;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// The one IMarketDataFeed the Engine ever sees. Every platform's feed always exists, but only the
/// CURRENTLY ACTIVE one is ever actually connected - this is what makes an in-process switch cheap
/// and safe: no second (or third) live socket burning a connection slot against another platform's
/// per-account cap, and no risk of more than one evaluating triggers off the same ticks and
/// double-firing orders (see IMarketDataFeed's own doc comment on why exactly one instance may
/// ever run).
///
/// RunAsync is called exactly ONCE by TickIngestionHostedService, for the life of the process - so
/// unlike a plain pass-through, this method has to internally supervise a hand-off: it runs
/// whichever platform's RunAsync is active, and when BrokerSwitchState.Changed fires, cancels just
/// that inner run (via a token scoped to "while this platform is active"), disposes its socket,
/// and loops to start the new platform's RunAsync - all without TickIngestionHostedService's own
/// call ever returning.
///
/// TickReceived/OrderUpdateReceived are subscribed on ALL THREE concrete feeds unconditionally at
/// construction, filtered by "is this platform the active one right now" before being re-raised.
/// In practice an inactive feed never actually raises anything (it isn't connected), but the
/// filter is a cheap belt-and-suspenders guard against a stray in-flight event landing in the
/// narrow window right at a switch. Note KotakMarketFeedClient's own OrderUpdateReceived never
/// actually fires - see its doc comment - so that filtered handler is inert for Kotak specifically,
/// but wiring it costs nothing and keeps this class's shape identical across all three platforms.
/// </summary>
public sealed class SwitchingMarketDataFeed : IMarketDataFeed
{
    private readonly BrokerSwitchState _state;
    private readonly KiteTickerClient _kite;
    private readonly DhanMarketFeedClient _dhan;
    private readonly KotakMarketFeedClient _kotak;
    private readonly ILogger<SwitchingMarketDataFeed> _logger;

    public SwitchingMarketDataFeed(
        BrokerSwitchState state,
        KiteTickerClient kite,
        DhanMarketFeedClient dhan,
        KotakMarketFeedClient kotak,
        ILogger<SwitchingMarketDataFeed> logger)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
        _logger = logger;

        _kite.TickReceived += t => { if (_state.Current == BrokerPlatform.Kite) TickReceived?.Invoke(t); };
        _dhan.TickReceived += t => { if (_state.Current == BrokerPlatform.Dhan) TickReceived?.Invoke(t); };
        _kotak.TickReceived += t => { if (_state.Current == BrokerPlatform.Kotak) TickReceived?.Invoke(t); };
        _kite.OrderUpdateReceived += () => { if (_state.Current == BrokerPlatform.Kite) OrderUpdateReceived?.Invoke(); };
        _dhan.OrderUpdateReceived += () => { if (_state.Current == BrokerPlatform.Dhan) OrderUpdateReceived?.Invoke(); };
        _kotak.OrderUpdateReceived += () => { if (_state.Current == BrokerPlatform.Kotak) OrderUpdateReceived?.Invoke(); };
    }

    public event Action<Tick>? TickReceived;
    public event Action? OrderUpdateReceived;

    private IMarketDataFeed Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken) =>
        Active.SubscribeAsync(instrumentTokens, cancellationToken);

    public Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken) =>
        Active.UnsubscribeAsync(instrumentTokens, cancellationToken);

    public IReadOnlyCollection<uint> GetSubscribedTokens() => Active.GetSubscribedTokens();

    public async Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken stoppingToken)
    {
        var seedTokens = instrumentTokens;

        while (!stoppingToken.IsCancellationRequested)
        {
            var platform = _state.Current;
            var feed = SelectFeed(platform);

            using var switchCts = new CancellationTokenSource();
            void OnChanged(BrokerPlatform _) => switchCts.Cancel();
            _state.Changed += OnChanged;

            try
            {
                // A switch could already have landed between reading Current above and subscribing
                // to Changed just now - if so there's nothing to run for `platform` any more.
                if (_state.Current == platform)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, switchCts.Token);
                    await feed.RunAsync(seedTokens, linked.Token);
                }
            }
            finally
            {
                _state.Changed -= OnChanged;
            }

            await SafeDisposeAsync(feed);

            if (stoppingToken.IsCancellationRequested)
                return;

            _logger.LogInformation("Market data feed switched from {Old} to {New}.", platform, _state.Current);
            seedTokens = Array.Empty<uint>(); // the new platform starts with nothing pre-seeded - spreads were cleared as part of the switch, see BrokerSwitchState
        }
    }

    private IMarketDataFeed SelectFeed(BrokerPlatform platform) => platform switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    private async Task SafeDisposeAsync(IMarketDataFeed feed)
    {
        try
        {
            await feed.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing the outgoing market data feed during a broker switch - continuing anyway.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _kite.DisposeAsync();
        await _dhan.DisposeAsync();
        await _kotak.DisposeAsync();
    }
}
