using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Ticker;
using SpreadTrader.KiteIntegration.Ticker;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// The one IMarketDataFeed the Engine ever sees. Both platforms' feeds always exist, but only the
/// CURRENTLY ACTIVE one is ever actually connected - this is what makes an in-process switch cheap
/// and safe: no second live socket burning a connection slot against Kite's/Dhan's per-account cap,
/// and no risk of both evaluating triggers off the same ticks and double-firing orders (see
/// IMarketDataFeed's own doc comment on why exactly one instance may ever run).
///
/// RunAsync is called exactly ONCE by TickIngestionHostedService, for the life of the process - so
/// unlike a plain pass-through, this method has to internally supervise a hand-off: it runs
/// whichever platform's RunAsync is active, and when BrokerSwitchState.Changed fires, cancels just
/// that inner run (via a token scoped to "while this platform is active"), disposes its socket,
/// and loops to start the new platform's RunAsync - all without TickIngestionHostedService's own
/// call ever returning.
///
/// TickReceived/OrderUpdateReceived are subscribed on BOTH concrete feeds unconditionally at
/// construction, filtered by "is this platform the active one right now" before being re-raised.
/// In practice the inactive feed never actually raises anything (it isn't connected), but the
/// filter is a cheap belt-and-suspenders guard against a stray in-flight event landing in the
/// narrow window right at a switch.
/// </summary>
public sealed class SwitchingMarketDataFeed : IMarketDataFeed
{
    private readonly BrokerSwitchState _state;
    private readonly KiteTickerClient _kite;
    private readonly DhanMarketFeedClient _dhan;
    private readonly ILogger<SwitchingMarketDataFeed> _logger;

    public SwitchingMarketDataFeed(
        BrokerSwitchState state, KiteTickerClient kite, DhanMarketFeedClient dhan, ILogger<SwitchingMarketDataFeed> logger)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _logger = logger;

        _kite.TickReceived += t => { if (_state.Current == BrokerPlatform.Kite) TickReceived?.Invoke(t); };
        _dhan.TickReceived += t => { if (_state.Current == BrokerPlatform.Dhan) TickReceived?.Invoke(t); };
        _kite.OrderUpdateReceived += () => { if (_state.Current == BrokerPlatform.Kite) OrderUpdateReceived?.Invoke(); };
        _dhan.OrderUpdateReceived += () => { if (_state.Current == BrokerPlatform.Dhan) OrderUpdateReceived?.Invoke(); };
    }

    public event Action<Tick>? TickReceived;
    public event Action? OrderUpdateReceived;

    private IMarketDataFeed Active => _state.Current == BrokerPlatform.Kite ? _kite : _dhan;

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
            var feed = platform == BrokerPlatform.Kite ? (IMarketDataFeed)_kite : _dhan;

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
    }
}
