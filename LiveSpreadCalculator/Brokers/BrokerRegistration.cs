using LiveSpreadCalculator.Realtime;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.DhanIntegration;
using SpreadTrader.DhanIntegration.Auth;
using SpreadTrader.DhanIntegration.Instruments;
using SpreadTrader.DhanIntegration.Orders;
using SpreadTrader.DhanIntegration.Positions;
using SpreadTrader.DhanIntegration.Ticker;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KiteIntegration.Auth;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Orders;
using SpreadTrader.KiteIntegration.Positions;
using SpreadTrader.KiteIntegration.Ticker;

namespace LiveSpreadCalculator.Brokers;

/// <summary>
/// The one place the <c>Broker:Platform</c> flag takes effect. Exactly one broker's implementations
/// are registered against the seven interfaces in SpreadTrader.Core.Brokers, and nothing downstream
/// - not the engine, not a controller, not a store - can tell which one was chosen.
///
/// That is the whole point of the arrangement: adding or switching a platform is a change to this
/// file plus a self-contained integration project, never a change to trading logic. SpreadTrader.Engine
/// does not even reference a broker project (check its .csproj), so a compile failure is the
/// backstop against broker-specific code leaking back into the engine.
///
/// The choice is made once, at startup, rather than per-call. There is deliberately no runtime
/// switch: a half-Kite/half-Dhan state would be a genuinely dangerous thing to have in a process
/// that places real orders, and per-call branching would put a platform check in the hot tick path
/// for no benefit.
/// </summary>
public static class BrokerRegistration
{
    /// <summary>Instrument dumps are large downloads (Dhan's detailed scrip master especially), so
    /// they get a client with a much longer timeout than the default 100 seconds - a truncated
    /// instrument master means symbols silently fail to resolve.</summary>
    private const string InstrumentMasterHttpClient = "BrokerInstrumentMaster";

    private static readonly TimeSpan InstrumentMasterTimeout = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddBrokerIntegration(
        this IServiceCollection services,
        IConfiguration configuration,
        McxOptions mcxOptions,
        string futuresCsvPath,
        string? dhanScripMasterCachePath = null)
    {
        var brokerOptions = configuration.GetSection(BrokerOptions.SectionName).Get<BrokerOptions>() ?? new BrokerOptions();
        services.Configure<BrokerOptions>(configuration.GetSection(BrokerOptions.SectionName));

        services.AddHttpClient();
        services.AddHttpClient(InstrumentMasterHttpClient, c => c.Timeout = InstrumentMasterTimeout);

        // Real-time "your broker session just died" push. Shared by both platforms' auth-failure
        // handlers, which is why it is registered here rather than inside either branch.
        services.AddSingleton<IBrokerAuthNotifier, SignalRBrokerAuthNotifier>();

        return brokerOptions.Platform switch
        {
            BrokerPlatform.Dhan => services.AddDhan(configuration, mcxOptions, futuresCsvPath, dhanScripMasterCachePath),
            BrokerPlatform.Kite => services.AddKite(configuration, mcxOptions, futuresCsvPath),
            _ => throw new InvalidOperationException(
                $"Unknown Broker:Platform '{brokerOptions.Platform}'. Supported values are 'Kite' and 'Dhan'."),
        };
    }

    // ---- Zerodha Kite ---------------------------------------------------------------------------

    private static IServiceCollection AddKite(
        this IServiceCollection services,
        IConfiguration configuration,
        McxOptions mcxOptions,
        string futuresCsvPath)
    {
        services.Configure<KiteOptions>(configuration.GetSection(KiteOptions.SectionName));

        services.AddSingleton<KiteSession>();
        services.AddSingleton<IBrokerSession>(sp => sp.GetRequiredService<KiteSession>());

        // Kite documents roughly 3-5 order placements per second; 3 leaves headroom.
        services.AddSingleton(new RateLimiter(maxOperationsPerSecond: 3));

        services.AddSingleton(sp => new InstrumentMasterService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstrumentMasterHttpClient),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<ILogger<InstrumentMasterService>>(),
            futuresCsvPath,
            // ["NFO"] when the MCX add-on is off (unchanged behaviour), ["NFO","MCX"] when on.
            mcxOptions.ResolveExchanges()));
        services.AddSingleton<IBrokerInstrumentMaster>(sp => sp.GetRequiredService<InstrumentMasterService>());

        services.AddSingleton(sp => new KiteAuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteAuthService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<ILogger<KiteAuthService>>()));
        services.AddSingleton<IBrokerAuthService>(sp => sp.GetRequiredService<KiteAuthService>());

        // Detects Kite's TokenException (access token invalid/expired) at every call site that hits
        // Kite's REST API, clears the session immediately, and pushes IBrokerAuthNotifier so
        // connected dashboards flip to "not logged in" the instant a real API call fails - not just
        // on next page load. See KiteAuthFailureHandler's own doc comment.
        services.AddSingleton<KiteAuthFailureHandler>();

        services.AddSingleton(sp => new KiteOrderGateway(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteOrderGateway)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<RateLimiter>(),
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<KiteOrderGateway>>()));
        services.AddSingleton<IOrderGateway>(sp => sp.GetRequiredService<KiteOrderGateway>());

        services.AddSingleton(sp => new KiteMarginService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteMarginService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<KiteMarginService>>()));
        services.AddSingleton<IMarginService>(sp => sp.GetRequiredService<KiteMarginService>());

        services.AddSingleton(sp => new KitePositionsService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KitePositionsService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<InstrumentMasterService>(),
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<ILogger<KitePositionsService>>(),
            mcxOptions));
        services.AddSingleton<IPositionsService>(sp => sp.GetRequiredService<KitePositionsService>());

        services.AddSingleton<KiteTickerClient>();
        services.AddSingleton<IMarketDataFeed>(sp => sp.GetRequiredService<KiteTickerClient>());

        return services;
    }

    // ---- DhanHQ ---------------------------------------------------------------------------------

    private static IServiceCollection AddDhan(
        this IServiceCollection services,
        IConfiguration configuration,
        McxOptions mcxOptions,
        string futuresCsvPath,
        string? scripMasterCachePath)
    {
        services.Configure<DhanOptions>(configuration.GetSection(DhanOptions.SectionName));
        var dhanOptions = configuration.GetSection(DhanOptions.SectionName).Get<DhanOptions>() ?? new DhanOptions();

        services.AddSingleton<DhanSession>();
        services.AddSingleton<IBrokerSession>(sp => sp.GetRequiredService<DhanSession>());

        services.AddSingleton(new RateLimiter(maxOperationsPerSecond: dhanOptions.OrdersPerSecond));

        // Lets the positions service teach the feed client which segment a securityId lives in, so
        // an open position that isn't a future in the (futures-only) instrument master can still be
        // subscribed for live price/exit PNL - see DhanSegmentRegistry.
        services.AddSingleton<DhanSegmentRegistry>();

        // Detects a dead Dhan token (HTTP 401 / DH-901 on REST, disconnect code 807 on the feed) at
        // every call site, clears the session, and pushes the dashboard - see its doc comment.
        services.AddSingleton<DhanAuthFailureHandler>();

        // Every Dhan REST call goes through this: auth headers, dhanClientId injection, error
        // parsing and auth-failure routing all live in one place - see DhanApiClient.
        services.AddSingleton(sp => new DhanApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DhanApiClient)),
            sp.GetRequiredService<IOptions<DhanOptions>>(),
            sp.GetRequiredService<DhanSession>(),
            sp.GetRequiredService<DhanAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<DhanApiClient>>()));

        services.AddSingleton(sp => new DhanAuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DhanAuthService)),
            sp.GetRequiredService<IOptions<DhanOptions>>(),
            sp.GetRequiredService<DhanSession>(),
            sp.GetRequiredService<ILogger<DhanAuthService>>()));
        services.AddSingleton<IBrokerAuthService>(sp => sp.GetRequiredService<DhanAuthService>());

        // Only when Dhan:AuthMode is "Totp": mints and renews the session's access token with no
        // browser and no manual paste, ever - see the class doc comment for its full schedule. In
        // the default "Static" mode this is never registered, and behaviour is unchanged from
        // before this existed (DhanSession seeds itself from Dhan:AccessToken at construction).
        if (dhanOptions.UseTotpAuth)
            services.AddHostedService<DhanTotpAutoRenewalHostedService>();

        services.AddSingleton(sp => new DhanInstrumentMasterService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstrumentMasterHttpClient),
            sp.GetRequiredService<IOptions<DhanOptions>>(),
            sp.GetRequiredService<ILogger<DhanInstrumentMasterService>>(),
            futuresCsvPath,
            mcxOptions.ResolveExchanges(),
            scripMasterCachePath));
        services.AddSingleton<IBrokerInstrumentMaster>(sp => sp.GetRequiredService<DhanInstrumentMasterService>());

        services.AddSingleton(sp => new DhanOrderGateway(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<IBrokerInstrumentMaster>(),
            sp.GetRequiredService<RateLimiter>(),
            sp.GetRequiredService<ILogger<DhanOrderGateway>>()));
        services.AddSingleton<IOrderGateway>(sp => sp.GetRequiredService<DhanOrderGateway>());

        services.AddSingleton(sp => new DhanMarginService(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<IBrokerInstrumentMaster>(),
            sp.GetRequiredService<ILogger<DhanMarginService>>()));
        services.AddSingleton<IMarginService>(sp => sp.GetRequiredService<DhanMarginService>());

        services.AddSingleton(sp => new DhanPositionsService(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<IBrokerInstrumentMaster>(),
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<DhanSegmentRegistry>(),
            sp.GetRequiredService<ILogger<DhanPositionsService>>(),
            mcxOptions));
        services.AddSingleton<IPositionsService>(sp => sp.GetRequiredService<DhanPositionsService>());

        services.AddSingleton<DhanMarketFeedClient>();
        services.AddSingleton<IMarketDataFeed>(sp => sp.GetRequiredService<DhanMarketFeedClient>());

        return services;
    }
}
