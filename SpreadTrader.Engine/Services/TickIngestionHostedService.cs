using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Ticker;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Resolves every configured spread leg to a Kite instrument_token, then owns the single Kite
/// ticker WebSocket connection for the life of the process, writing every tick into the shared
/// ITickStore. Also exposes new tokens to the already-running ticker whenever a spread is added
/// later through the API (see ResolveAndSubscribeAsync, called from SpreadsController).
/// </summary>
public sealed class TickIngestionHostedService : BackgroundService
{
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly KiteTickerClient _tickerClient;
    private readonly ITickStore _tickStore;
    private readonly ILogger<TickIngestionHostedService> _logger;

    public TickIngestionHostedService(
        ISpreadDefinitionStore spreadStore,
        InstrumentMasterService instrumentMaster,
        KiteTickerClient tickerClient,
        ITickStore tickStore,
        ILogger<TickIngestionHostedService> logger)
    {
        _spreadStore = spreadStore;
        _instrumentMaster = instrumentMaster;
        _tickerClient = tickerClient;
        _tickStore = tickStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tickerClient.TickReceived += _tickStore.Update;
        try
        {
            await _instrumentMaster.EnsureLoadedAsync(stoppingToken);

            var tokens = new HashSet<uint>();
            foreach (var spread in _spreadStore.GetAll().Where(s => s.Enabled))
            {
                await ResolveLegAsync(spread.NearLeg, stoppingToken);
                await ResolveLegAsync(spread.FarLeg, stoppingToken);
                if (spread.NearLeg.InstrumentToken != 0) tokens.Add(spread.NearLeg.InstrumentToken);
                if (spread.FarLeg.InstrumentToken != 0) tokens.Add(spread.FarLeg.InstrumentToken);
            }

            if (tokens.Count == 0)
            {
                _logger.LogWarning(
                    "No spreads configured yet - the ticker will connect but subscribe to nothing. " +
                    "Configure spreads via POST /api/spreads and it will subscribe automatically.");
            }

            await _tickerClient.RunAsync(tokens, stoppingToken);
        }
        finally
        {
            _tickerClient.TickReceived -= _tickStore.Update;
        }
    }

    /// <summary>Resolves a spread's legs to instrument tokens and subscribes the running
    /// ticker to them immediately. Called by SpreadsController when a spread is added at
    /// runtime, so the dashboard doesn't require a restart to start streaming a new pair.</summary>
    public async Task ResolveAndSubscribeAsync(SpreadDefinition spread, CancellationToken cancellationToken)
    {
        await ResolveLegAsync(spread.NearLeg, cancellationToken);
        await ResolveLegAsync(spread.FarLeg, cancellationToken);

        var tokens = new List<uint>();
        if (spread.NearLeg.InstrumentToken != 0) tokens.Add(spread.NearLeg.InstrumentToken);
        if (spread.FarLeg.InstrumentToken != 0) tokens.Add(spread.FarLeg.InstrumentToken);

        if (tokens.Count > 0)
            await _tickerClient.SubscribeAsync(tokens, cancellationToken);
    }

    /// <summary>Subscribes the running ticker to a batch of already-resolved instrument tokens
    /// in a single subscribe + mode WebSocket round trip, instead of one round trip per token
    /// pair. Used by UniverseBuilderService when building tens/hundreds of spreads at once -
    /// calling ResolveAndSubscribeAsync per-spread there was firing hundreds of separate
    /// subscribe/mode messages back-to-back, which is what was causing some instruments to
    /// silently end up without depth (never got mode=full applied) even though the exchange
    /// itself had live depth for them.</summary>
    public Task SubscribeTokensAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken) =>
        _tickerClient.SubscribeAsync(instrumentTokens, cancellationToken);

    private async Task ResolveLegAsync(InstrumentConfig config, CancellationToken cancellationToken)
    {
        if (config.InstrumentToken != 0) return;
        try
        {
            await _instrumentMaster.ResolveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve instrument token for {Symbol}", config.TradingSymbol);
        }
    }
}
