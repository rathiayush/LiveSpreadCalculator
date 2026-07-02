using System.Text.Json.Serialization;
using LiveSpreadCalculator.Hubs;
using LiveSpreadCalculator.Realtime;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Store;
using SpreadTrader.Engine;
using SpreadTrader.Engine.Services;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KiteIntegration.Auth;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Orders;
using SpreadTrader.KiteIntegration.Ticker;

var builder = WebApplication.CreateBuilder(args);

const string DashboardCorsPolicy = "Dashboard";

builder.Services.Configure<KiteOptions>(builder.Configuration.GetSection(KiteOptions.SectionName));
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.SectionName));

// ---- State (Core) --------------------------------------------------------------------------
// Ticks and spread definitions are in-memory only, by design - a restart loses them (re-add
// spreads via POST /api/spreads). Trigger rules are the one thing worth surviving a restart, so
// they're backed by a small CSV file (see CsvTriggerRuleStore) instead of pure in-memory - a
// deliberate stopgap, not a real database.
var triggersCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:TriggersCsvPath"] ?? "App_Data/triggers.csv");
var futuresCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:FuturesCsvPath"] ?? "App_Data/futures_instruments.csv");
// The universe builder pulls its symbol list live from the Kite instrument master (every stock
// currently listed for NFO futures - see InstrumentMasterService.GetAllFuturesUnderlyings) rather
// than a hand-maintained CSV. How many symbols get built is configurable per-request via
// POST /api/universe/build?maxSymbols=N; this is just where the resolved output gets written -
// see UniverseBuilderService.
var universeCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:UniverseCsvPath"] ?? "App_Data/universe_futures.csv");

builder.Services.AddSingleton<ITickStore, InMemoryTickStore>();
builder.Services.AddSingleton<ITriggerRuleStore>(_ => new CsvTriggerRuleStore(triggersCsvPath));
builder.Services.AddSingleton<ISpreadDefinitionStore, InMemorySpreadDefinitionStore>();
builder.Services.AddSingleton<ISpreadSnapshotCache, InMemorySpreadSnapshotCache>();
builder.Services.AddSingleton<ISpreadRankingStore, InMemorySpreadRankingStore>();

// ---- Kite integration -----------------------------------------------------------------------
builder.Services.AddHttpClient();
builder.Services.AddSingleton<KiteSession>();
builder.Services.AddSingleton(new RateLimiter(maxOperationsPerSecond: 3));

builder.Services.AddSingleton(sp => new InstrumentMasterService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(InstrumentMasterService)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<ILogger<InstrumentMasterService>>(),
    futuresCsvPath));

builder.Services.AddSingleton(sp => new KiteAuthService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteAuthService)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>()));

builder.Services.AddSingleton(sp => new KiteOrderGateway(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteOrderGateway)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>(),
    sp.GetRequiredService<RateLimiter>(),
    sp.GetRequiredService<ILogger<KiteOrderGateway>>()));
builder.Services.AddSingleton<IOrderGateway>(sp => sp.GetRequiredService<KiteOrderGateway>());

builder.Services.AddSingleton(sp => new KiteMarginService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteMarginService)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>(),
    sp.GetRequiredService<ILogger<KiteMarginService>>()));

builder.Services.AddSingleton<KiteTickerClient>();

// ---- Engine (singleton pipeline: ingest -> calculate -> evaluate -> dispatch) ---------------
// TickIngestionHostedService is also registered as itself (not just IHostedService) because
// SpreadsController calls ResolveAndSubscribeAsync on it directly when a spread is added at
// runtime - see the comment on that method for why.
builder.Services.AddSingleton<TriggerEventChannel>();
builder.Services.AddSingleton<TickIngestionHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TickIngestionHostedService>());
builder.Services.AddHostedService<SpreadCalculationHostedService>();
builder.Services.AddHostedService<OrderDispatchHostedService>();
builder.Services.AddHostedService<SpreadRankingHostedService>();

builder.Services.AddSingleton<ISpreadPublisher, SignalRSpreadPublisher>();
builder.Services.AddSingleton<ITriggerEventSink, SignalRTriggerEventSink>();

// ---- Universe builder + bulk trigger template ---------------------------------------------
builder.Services.AddSingleton(sp => new UniverseBuilderService(
    sp.GetRequiredService<InstrumentMasterService>(),
    sp.GetRequiredService<ISpreadDefinitionStore>(),
    sp.GetRequiredService<TickIngestionHostedService>(),
    sp.GetRequiredService<ILogger<UniverseBuilderService>>(),
    universeCsvPath));
builder.Services.AddSingleton<BulkTriggerTemplateService>();

// ---- Web / API / real-time ------------------------------------------------------------------
builder.Services.AddSignalR();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var dashboardOrigin = builder.Configuration["Dashboard:BaseUrl"] ?? "https://localhost:63061";
builder.Services.AddCors(options =>
{
    options.AddPolicy(DashboardCorsPolicy, policy => policy
        .WithOrigins(dashboardOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()); // SignalR's negotiate handshake needs credentialed CORS
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors(DashboardCorsPolicy);
app.UseAuthorization();

app.MapControllers();
app.MapHub<SpreadHub>("/hubs/spreads");
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })); // used by Dockerfile HEALTHCHECK / cloud liveness probes

app.Run();
