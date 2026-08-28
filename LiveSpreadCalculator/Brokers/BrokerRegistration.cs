using LiveSpreadCalculator.Brokers.Switching;
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
using SpreadTrader.KotakIntegration;
using SpreadTrader.KotakIntegration.Auth;
using SpreadTrader.KotakIntegration.Instruments;
using SpreadTrader.KotakIntegration.Orders;
using SpreadTrader.KotakIntegration.Positions;
using SpreadTrader.KotakIntegration.Ticker;

namespace LiveSpreadCalculator.Brokers;

/// <summary>
/// Wires up Kite's, Dhan's AND Kotak's implementations of the seven interfaces in
/// SpreadTrader.Core.Brokers, always - not exactly one, chosen at startup, the way this used to
/// work. Every interface is bound to a "Switching*" wrapper (see the Switching folder) that reads
/// BrokerSwitchState.Current on every call and forwards to whichever platform's concrete
/// instance is presently active. That is what lets BrokerPlatformController switch platforms with
/// a plain in-process call - BrokerSwitchState.SwitchTo - instead of restarting the process.
///
/// Consequence worth knowing: since every platform is always constructed, every platform's config
/// section needs to be at least well-formed enough not to throw eagerly (none of these constructors
/// validate credentials - see each auth service's own "not configured" exceptions, thrown lazily
/// at first use, same as before). A deployment that only ever intends to use one platform can leave
/// the others' credentials blank; that platform will simply fail to authenticate if ever switched
/// to, exactly like an unconfigured platform already fails today - it just doesn't stop the OTHER
/// platforms' services from constructing and running normally.
///
/// KOTAK IS DELIBERATELY NOT SELECTABLE FROM THE DASHBOARD - BrokerPlatformPage.jsx's PLATFORMS
/// list only offers Kite/Dhan by design (see that file's comment). Switching to Kotak is a
/// backend-only action: POST /api/settings/broker-platform with {"Platform":"Kotak"}, or setting
/// Broker__Platform=Kotak before boot. BrokerPlatformController itself needed no code change for
/// this - Enum.TryParse&lt;BrokerPlatform&gt; already accepts "Kotak" once the enum has the member;
/// only the frontend's own dropdown omits it.
///
/// SpreadTrader.Engine still never references any broker project directly - it only depends on
/// the interfaces, now satisfied by the Switching* wrappers instead of a single concrete
/// implementation. That boundary is what makes PositionsCache/TickIngestionHostedService/etc.
/// completely unaware that a switch can even happen.
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
        string? dhanScripMasterCachePath,
        string brokerPlatformOverridePath)
    {
        var brokerOptions = configuration.GetSection(BrokerOptions.SectionName).Get<BrokerOptions>() ?? new BrokerOptions();
        services.Configure<BrokerOptions>(configuration.GetSection(BrokerOptions.SectionName));

        services.AddHttpClient();
        services.AddHttpClient(InstrumentMasterHttpClient, c => c.Timeout = InstrumentMasterTimeout);

        // Real-time "your broker session just died" push. Shared by both platforms' auth-failure
        // handlers, which is why it is registered here rather than inside either branch.
        services.AddSingleton<IBrokerAuthNotifier, SignalRBrokerAuthNotifier>();

        services.AddKite(configuration, mcxOptions, futuresCsvPath);
        services.AddDhan(configuration, mcxOptions, futuresCsvPath, dhanScripMasterCachePath);
        services.AddKotak(configuration, mcxOptions, futuresCsvPath);

        // A broker switch made from the dashboard (see BrokerPlatformController) writes its choice
        // to this file, so it survives a later literal restart too - see
        // BrokerPlatformOverrideStore's doc comment. Takes precedence over Broker:Platform when
        // present; falls back to it (defaulting to Kite) otherwise, so an existing deployment that
        // has never used the switch behaves exactly as before.
        var initialPlatform = BrokerPlatformOverrideStore.TryRead(brokerPlatformOverridePath) ?? brokerOptions.Platform;
        services.AddSingleton(sp => new BrokerSwitchState(
            initialPlatform,
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<ISpreadDefinitionStore>(),
            brokerPlatformOverridePath,
            sp.GetRequiredService<ILogger<BrokerSwitchState>>()));
        services.AddSingleton<IBrokerSwitchNotifier>(sp => sp.GetRequiredService<BrokerSwitchState>());

        services.AddSingleton<IBrokerSession, SwitchingBrokerSession>();
        services.AddSingleton<IBrokerAuthService, SwitchingBrokerAuthService>();
        services.AddSingleton<IBrokerInstrumentMaster, SwitchingInstrumentMaster>();
        services.AddSingleton<IMarketDataFeed, SwitchingMarketDataFeed>();
        services.AddSingleton<IOrderGateway, SwitchingOrderGateway>();
        services.AddSingleton<IMarginService, SwitchingMarginService>();
        services.AddSingleton<IPositionsService, SwitchingPositionsService>();

        return services;
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

        // Kite documents roughly 3-5 order placements per second; 3 leaves headroom. A plain local
        // variable, not a DI registration - Dhan's OrderGateway needs its OWN RateLimiter instance
        // with a completely different rate, and both are now always constructed together, so
        // registering either one as the bare `RateLimiter` type would make GetRequiredService<RateLimiter>()
        // ambiguous between them (the container just returns whichever was registered last, silently
        // giving one gateway the other's rate limit).
        var kiteRateLimiter = new RateLimiter(maxOperationsPerSecond: 3);

        services.AddSingleton(sp => new InstrumentMasterService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstrumentMasterHttpClient),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<ILogger<InstrumentMasterService>>(),
            futuresCsvPath,
            // ["NFO"] when the MCX add-on is off (unchanged behaviour), ["NFO","MCX"] when on.
            mcxOptions.ResolveExchanges()));

        services.AddSingleton(sp => new KiteAuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteAuthService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<ILogger<KiteAuthService>>()));

        // Detects Kite's TokenException (access token invalid/expired) at every call site that hits
        // Kite's REST API, clears the session immediately, and pushes IBrokerAuthNotifier so
        // connected dashboards flip to "not logged in" the instant a real API call fails - not just
        // on next page load. See KiteAuthFailureHandler's own doc comment.
        services.AddSingleton<KiteAuthFailureHandler>();

        services.AddSingleton(sp => new KiteOrderGateway(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteOrderGateway)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            kiteRateLimiter,
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<KiteOrderGateway>>()));

        services.AddSingleton(sp => new KiteMarginService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteMarginService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<KiteMarginService>>()));

        services.AddSingleton(sp => new KitePositionsService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KitePositionsService)),
            sp.GetRequiredService<IOptions<KiteOptions>>(),
            sp.GetRequiredService<KiteSession>(),
            sp.GetRequiredService<KiteAuthFailureHandler>(),
            sp.GetRequiredService<InstrumentMasterService>(),
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<ILogger<KitePositionsService>>(),
            mcxOptions));

        services.AddSingleton<KiteTickerClient>();

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

        // See kiteRateLimiter's comment in AddKite above for why this is a local variable, not a
        // DI registration.
        var dhanRateLimiter = new RateLimiter(maxOperationsPerSecond: dhanOptions.OrdersPerSecond);

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

        // Only when Dhan:AuthMode is "Totp": mints and renews the session's access token with no
        // browser and no manual paste, ever - see the class doc comment for its full schedule. This
        // now runs regardless of whether Dhan is the CURRENTLY ACTIVE platform - deliberately, so a
        // deployment that keeps Dhan configured as a switch target has it already authenticated the
        // moment it's switched to, instead of paying a fresh-login delay at switch time.
        if (dhanOptions.UseTotpAuth)
            services.AddHostedService<DhanTotpAutoRenewalHostedService>();

        services.AddSingleton(sp => new DhanInstrumentMasterService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstrumentMasterHttpClient),
            sp.GetRequiredService<IOptions<DhanOptions>>(),
            sp.GetRequiredService<ILogger<DhanInstrumentMasterService>>(),
            futuresCsvPath,
            mcxOptions.ResolveExchanges(),
            scripMasterCachePath));

        // CDV feature (see CdvHostedService) - always points at Dhan's own instrument index,
        // independent of which platform BrokerSwitchState currently has active for trading, since
        // Dhan is always constructed regardless (see this method's class doc comment) - see
        // IOptionChainSource's doc comment for why this is Dhan-only rather than routed through
        // the switching layer.
        services.AddSingleton<IOptionChainSource>(sp => sp.GetRequiredService<DhanInstrumentMasterService>());

        // Same "always Dhan, regardless of which platform is active" reasoning as IOptionChainSource
        // above - lets CdvHostedService teach the live feed which segment its option tokens belong
        // to (see IInstrumentSegmentRegistry's doc comment for why that's needed at all).
        services.AddSingleton<IInstrumentSegmentRegistry>(sp => sp.GetRequiredService<DhanSegmentRegistry>());

        services.AddSingleton(sp => new DhanOrderGateway(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<DhanInstrumentMasterService>(),
            dhanRateLimiter,
            sp.GetRequiredService<ILogger<DhanOrderGateway>>()));

        services.AddSingleton(sp => new DhanMarginService(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<DhanInstrumentMasterService>(),
            sp.GetRequiredService<ILogger<DhanMarginService>>()));

        services.AddSingleton(sp => new DhanPositionsService(
            sp.GetRequiredService<DhanApiClient>(),
            sp.GetRequiredService<DhanInstrumentMasterService>(),
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<DhanSegmentRegistry>(),
            sp.GetRequiredService<ILogger<DhanPositionsService>>(),
            mcxOptions));

        services.AddSingleton<DhanMarketFeedClient>();

        return services;
    }

    // ---- Kotak Neo ------------------------------------------------------------------------------

    private static IServiceCollection AddKotak(
        this IServiceCollection services,
        IConfiguration configuration,
        McxOptions mcxOptions,
        string futuresCsvPath)
    {
        services.Configure<KotakOptions>(configuration.GetSection(KotakOptions.SectionName));
        var kotakOptions = configuration.GetSection(KotakOptions.SectionName).Get<KotakOptions>() ?? new KotakOptions();

        services.AddSingleton<KotakSession>();

        // See kiteRateLimiter's comment in AddKite above for why this is a local variable, not a
        // DI registration.
        var kotakRateLimiter = new RateLimiter(maxOperationsPerSecond: kotakOptions.OrdersPerSecond);

        // Detects a dead Kotak session at every call site, clears it, and pushes the dashboard -
        // see its doc comment for the (best-effort, unconfirmed) detection heuristic.
        services.AddSingleton<KotakAuthFailureHandler>();

        // Every session-authenticated Kotak REST call goes through this - see KotakApiClient.
        services.AddSingleton(sp => new KotakApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KotakApiClient)),
            sp.GetRequiredService<IOptions<KotakOptions>>(),
            sp.GetRequiredService<KotakSession>(),
            sp.GetRequiredService<KotakAuthFailureHandler>(),
            sp.GetRequiredService<ILogger<KotakApiClient>>()));

        services.AddSingleton(sp => new KotakAuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KotakAuthService)),
            sp.GetRequiredService<IOptions<KotakOptions>>(),
            sp.GetRequiredService<KotakSession>(),
            sp.GetRequiredService<ILogger<KotakAuthService>>()));

        // Kotak has no static-token mode at all (unlike Dhan) - this is the only way a Kotak
        // session is ever obtained, so it always runs, not gated behind an AuthMode check.
        services.AddHostedService<KotakSessionAutoRenewalHostedService>();

        services.AddSingleton(sp => new KotakInstrumentMasterService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstrumentMasterHttpClient),
            sp.GetRequiredService<KotakApiClient>(),
            sp.GetRequiredService<IOptions<KotakOptions>>(),
            sp.GetRequiredService<ILogger<KotakInstrumentMasterService>>(),
            futuresCsvPath,
            // Kotak has no MCX segment at all (see KotakSymbology) - MCX is filtered out inside
            // KotakInstrumentMasterService.ResolveRequestedSegments even if mcxOptions enables it
            // for the other two platforms, so passing the same resolved exchange list here is safe.
            mcxOptions.ResolveExchanges()));

        services.AddSingleton(sp => new KotakOrderGateway(
            sp.GetRequiredService<KotakApiClient>(),
            kotakRateLimiter,
            sp.GetRequiredService<ILogger<KotakOrderGateway>>()));

        services.AddSingleton(sp => new KotakMarginService(
            sp.GetRequiredService<KotakApiClient>(),
            sp.GetRequiredService<ILogger<KotakMarginService>>()));

        services.AddSingleton(sp => new KotakPositionsService(
            sp.GetRequiredService<KotakApiClient>(),
            sp.GetRequiredService<KotakInstrumentMasterService>(),
            sp.GetRequiredService<ITickStore>(),
            sp.GetRequiredService<ILogger<KotakPositionsService>>()));

        services.AddSingleton<KotakMarketFeedClient>();

        return services;
    }
}
