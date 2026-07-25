using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Builds the tradeable universe automatically - once at process startup, and again every time the
/// operator switches broker platform (see IBrokerSwitchNotifier) - so "click Build universe" is no
/// longer a manual step required after every restart or switch. Runs UniverseBuilderService.BuildAsync
/// (unrestricted - every NFO futures-listed stock, same as the Symbols page's default) and, when the
/// MCX add-on is enabled, BuildMcxAsync right after it. The manual "Build universe" / "Build MCX
/// universe" buttons on the Symbols page still work exactly as before - this only removes the ONE
/// click that switching or restarting used to always require before anything streamed.
///
/// EVENT-DRIVEN, same shape as PositionsCache: a broker switch is signalled through a
/// SemaphoreSlim, not awaited directly inside BrokerSwitchState.SwitchTo's synchronous call chain -
/// a universe build can take several seconds (resolving every symbol's near/far leg against the
/// broker's instrument master), and SwitchTo is called synchronously from the HTTP request handling
/// POST /api/settings/broker-platform. Raising Changed must stay fast; the actual build happens here,
/// in the background, once this loop wakes up.
///
/// Failures (network hiccup fetching the instrument master, broker not yet authenticated) are
/// retried with backoff rather than left as a permanent "no universe" state - the operator never
/// has to notice and manually retry what used to be a manual click anyway.
/// </summary>
public sealed class AutoUniverseBuilderHostedService : BackgroundService
{
    private static readonly TimeSpan RetryBackoffInitial = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBackoffMax = TimeSpan.FromMinutes(2);

    private readonly UniverseBuilderService _universeBuilder;
    private readonly McxOptions _mcx;
    private readonly IBrokerSwitchNotifier? _brokerSwitchNotifier;
    private readonly ILogger<AutoUniverseBuilderHostedService> _logger;

    private readonly SemaphoreSlim _buildSignal = new(0, 1);
    private int _buildPending;

    public AutoUniverseBuilderHostedService(
        UniverseBuilderService universeBuilder,
        McxOptions mcx,
        ILogger<AutoUniverseBuilderHostedService> logger,
        IBrokerSwitchNotifier? brokerSwitchNotifier = null)
    {
        _universeBuilder = universeBuilder;
        _mcx = mcx;
        _logger = logger;
        _brokerSwitchNotifier = brokerSwitchNotifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_brokerSwitchNotifier is not null) _brokerSwitchNotifier.Changed += OnBrokerSwitched;
        try
        {
            // STARTUP build, retried with backoff until it succeeds - there is always exactly one
            // "no universe built yet" state to fill in, whether this is a fresh process or the
            // first build ever attempted.
            await BuildUntilSuccessAsync(stoppingToken);

            // Every later trigger is a broker switch - block until one arrives, exactly like
            // PositionsCache blocks on order updates between resyncs.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _buildSignal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Interlocked.Exchange(ref _buildPending, 0);
                await BuildUntilSuccessAsync(stoppingToken);
            }
        }
        finally
        {
            if (_brokerSwitchNotifier is not null) _brokerSwitchNotifier.Changed -= OnBrokerSwitched;
        }
    }

    /// <summary>Called synchronously from BrokerSwitchState.SwitchTo - must stay non-blocking, same
    /// contract as PositionsCache.OnOrderUpdate. Coalesces a burst of rapid switches into a single
    /// pending build (only the LATEST platform matters by the time the loop wakes up, since
    /// UniverseBuilderService always builds against whichever platform is active at that moment).
    /// </summary>
    private void OnBrokerSwitched(BrokerPlatform platform)
    {
        if (Interlocked.CompareExchange(ref _buildPending, 1, 0) == 0)
        {
            try { _buildSignal.Release(); }
            catch (SemaphoreFullException) { /* already armed - coalesced */ }
        }
    }

    private async Task BuildUntilSuccessAsync(CancellationToken stoppingToken)
    {
        var backoff = RetryBackoffInitial;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _universeBuilder.BuildAsync(maxSymbols: null, stoppingToken);
                _logger.LogInformation(
                    "Automatic universe build complete: {Included}/{Requested} symbols, {Skipped} skipped.",
                    result.Included.Count, result.RequestedCount, result.Skipped.Count);

                if (_mcx.Enabled)
                {
                    var mcxResult = await _universeBuilder.BuildMcxAsync(stoppingToken);
                    _logger.LogInformation(
                        "Automatic MCX universe build complete: {Included}/{Requested} spreads, {Skipped} skipped.",
                        mcxResult.Included.Count, mcxResult.RequestedCount, mcxResult.Skipped.Count);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Automatic universe build failed - retrying in {Seconds}s. The manual Build universe " +
                    "button on the Symbols page works meanwhile if you don't want to wait.",
                    backoff.TotalSeconds);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, RetryBackoffMax.TotalSeconds));
            }
        }
    }
}
