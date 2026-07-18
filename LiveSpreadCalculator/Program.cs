using System.Text;
using System.Text.Json.Serialization;
using LiveSpreadCalculator.Auth;
using LiveSpreadCalculator.Hubs;
using LiveSpreadCalculator.Notifications;
using LiveSpreadCalculator.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Realtime;
using SpreadTrader.Core.Store;
using SpreadTrader.Engine;
using SpreadTrader.Engine.Services;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KiteIntegration.Auth;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Orders;
using SpreadTrader.KiteIntegration.Positions;
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
// Exit triggers (rules that CLOSE an existing position) live in a completely separate CSV file
// from entry triggers - see IExitTriggerRuleStore's doc comment for why they're never mixed into
// one shared table.
var exitTriggersCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:ExitTriggersCsvPath"] ?? "App_Data/exit_triggers.csv");
// PNL alerts (see PnlAlertRule/CsvPnlAlertStore) - same "small CSV so it survives a restart"
// treatment as trigger rules, kept in its own file since the row shape is unrelated.
var pnlAlertsCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:PnlAlertsCsvPath"] ?? "App_Data/pnl-alerts.csv");
var futuresCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:FuturesCsvPath"] ?? "App_Data/futures_instruments.csv");
// The universe builder pulls its symbol list live from the Kite instrument master (every stock
// currently listed for NFO futures - see InstrumentMasterService.GetAllFuturesUnderlyings) rather
// than a hand-maintained CSV. How many symbols get built is configurable per-request via
// POST /api/universe/build?maxSymbols=N; this is just where the resolved output gets written -
// see UniverseBuilderService.
var universeCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:UniverseCsvPath"] ?? "App_Data/universe_futures.csv");
// Allow-list of who may log in and obtain a JWT (email,password rows). See CsvUserStore.
var usersCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:UsersCsvPath"] ?? "App_Data/users.csv");

builder.Services.AddSingleton<ITickStore, InMemoryTickStore>();
builder.Services.AddSingleton<ITriggerRuleStore>(sp =>
    new CsvTriggerRuleStore(triggersCsvPath, sp.GetRequiredService<ILogger<CsvTriggerRuleStore>>()));
builder.Services.AddSingleton<IExitTriggerRuleStore>(sp =>
    new CsvExitTriggerRuleStore(exitTriggersCsvPath, sp.GetRequiredService<ILogger<CsvTriggerRuleStore>>()));
builder.Services.AddSingleton<IPnlAlertStore>(sp =>
    new CsvPnlAlertStore(pnlAlertsCsvPath, sp.GetRequiredService<ILogger<CsvPnlAlertStore>>()));
builder.Services.AddSingleton<ISpreadDefinitionStore, InMemorySpreadDefinitionStore>();
builder.Services.AddSingleton<ISpreadSnapshotCache, InMemorySpreadSnapshotCache>();
builder.Services.AddSingleton<ISpreadRankingStore, InMemorySpreadRankingStore>();
// Rolling in-memory history of fired triggers/execution outcomes, so GET /api/triggers/activity
// can reseed the Trigger Activity feed after a browser refresh - see TriggerActivityEntry.
builder.Services.AddSingleton<ITriggerActivityStore, InMemoryTriggerActivityStore>();

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

// Detects Kite's TokenException (access token invalid/expired) at every call site that hits
// Kite's REST API, clears KiteSession immediately, and pushes IKiteAuthNotifier (below) so
// connected dashboards flip to "not logged in" the instant a real API call fails - not just on
// next page load. See KiteAuthFailureHandler's own doc comment.
builder.Services.AddSingleton<KiteAuthFailureHandler>();
builder.Services.AddSingleton<IKiteAuthNotifier, SignalRKiteAuthNotifier>();

builder.Services.AddSingleton(sp => new KiteOrderGateway(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteOrderGateway)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>(),
    sp.GetRequiredService<RateLimiter>(),
    sp.GetRequiredService<KiteAuthFailureHandler>(),
    sp.GetRequiredService<ILogger<KiteOrderGateway>>()));
builder.Services.AddSingleton<IOrderGateway>(sp => sp.GetRequiredService<KiteOrderGateway>());

builder.Services.AddSingleton(sp => new KiteMarginService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KiteMarginService)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>(),
    sp.GetRequiredService<KiteAuthFailureHandler>(),
    sp.GetRequiredService<ILogger<KiteMarginService>>()));

// Live positions/PNL + manual square-off (PositionsController) - see KitePositionsService's doc
// comment for why this exists (lets the dashboard replace checking PNL via the Kite app, which
// invalidates this app's own session).
builder.Services.AddSingleton(sp => new KitePositionsService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KitePositionsService)),
    sp.GetRequiredService<IOptions<KiteOptions>>(),
    sp.GetRequiredService<KiteSession>(),
    sp.GetRequiredService<KiteAuthFailureHandler>(),
    sp.GetRequiredService<InstrumentMasterService>(),
    sp.GetRequiredService<ITickStore>(),
    sp.GetRequiredService<ILogger<KitePositionsService>>()));

// Shared MARKET -> protected-LIMIT conversion (Kite rejects raw MARKET orders for F&O - see
// MarketProtectionService's doc comment). Used by both OrderDispatchHostedService (triggered
// baskets, registered below) and PositionsController (manual square-off).
builder.Services.AddSingleton<MarketProtectionService>();

builder.Services.AddSingleton<KiteTickerClient>();

// ---- Engine (singleton pipeline: ingest -> calculate -> evaluate -> dispatch) ---------------
// TickIngestionHostedService is also registered as itself (not just IHostedService) because
// SpreadsController calls ResolveAndSubscribeAsync on it directly when a spread is added at
// runtime - see the comment on that method for why.
builder.Services.AddSingleton<TriggerEventChannel>();
builder.Services.AddSingleton<TickIngestionHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TickIngestionHostedService>());
// Shared between SpreadCalculationHostedService (checks it before firing the next shot of a
// bounded multi-shot trigger) and OrderDispatchHostedService (marks/clears it around each
// dispatch) - see TriggerInFlightTracker's doc comment.
builder.Services.AddSingleton<TriggerInFlightTracker>();
builder.Services.AddHostedService<SpreadCalculationHostedService>();
builder.Services.AddHostedService<OrderDispatchHostedService>();
builder.Services.AddHostedService<SpreadRankingHostedService>();

builder.Services.AddSingleton<ISpreadPublisher, SignalRSpreadPublisher>();
builder.Services.AddSingleton<ITriggerEventSink, SignalRTriggerEventSink>();

// The single Kite positions poller for the whole process - see PositionsCache's doc comment for
// why this was extracted out of LivePositionsHostedService (so PnlAlertHostedService below doesn't
// need its own independent REST-polling loop hitting the same endpoint). Registered as itself (not
// just IHostedService), same reasoning/pattern as TickIngestionHostedService just above, so both
// LivePositionsHostedService and PnlAlertHostedService can inject the concrete type and call
// GetProjected() directly.
builder.Services.AddSingleton<PositionsCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PositionsCache>());

// Live PNL for the Positions & PNL dashboard page - see LivePositionsHostedService's doc comment.
// PositionsViewerTracker is the shared singleton SpreadHub (JoinPositions/LeavePositions) and
// LivePositionsHostedService both depend on, so the SignalR push can skip itself entirely while no
// browser tab has that page open - PositionsCache above keeps resyncing regardless.
builder.Services.AddSingleton<PositionsViewerTracker>();
builder.Services.AddSingleton<IPositionsPublisher, SignalRPositionsPublisher>();
builder.Services.AddHostedService<LivePositionsHostedService>();

// Reports what actually got bought/sold (and whether it succeeded) once OrderDispatchHostedService
// finishes acting on a fired trigger - see BasketExecutionEvent / IOrderExecutionSink. The single
// IOrderExecutionSink the engine depends on is a composite that fans the outcome out to the SignalR
// dashboard push and (when configured) a Telegram message, each isolated so one failing can't stop
// the other - see CompositeOrderExecutionSink / TelegramOrderExecutionSink.
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
// Bounds the fire-and-forget Telegram POST so a hung connection can't leave a background send
// pending forever (the send itself is never awaited by order dispatch - see TelegramOrderExecutionSink).
builder.Services.AddHttpClient(nameof(TelegramOrderExecutionSink), c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<SignalROrderExecutionSink>();
builder.Services.AddSingleton<TelegramOrderExecutionSink>();
builder.Services.AddSingleton<IOrderExecutionSink>(sp =>
{
    var sinks = new List<IOrderExecutionSink> { sp.GetRequiredService<SignalROrderExecutionSink>() };

    var telegram = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
    if (telegram.Enabled)
        sinks.Add(sp.GetRequiredService<TelegramOrderExecutionSink>());

    return new CompositeOrderExecutionSink(sinks, sp.GetRequiredService<ILogger<CompositeOrderExecutionSink>>());
});

// Standing "notify me on Telegram" watches on a spread's combined Exit PNL - see PnlAlertRule /
// PnlAlertHostedService. Reads from PositionsCache above (zero extra Kite REST calls of its own).
// Reuses the same TelegramOptions section configured above, through the generic IPnlAlertNotifier
// abstraction rather than TelegramOrderExecutionSink, since Engine (where PnlAlertHostedService
// lives) can't reference this web project's Notifications namespace directly - see
// IPnlAlertNotifier's doc comment.
builder.Services.AddHttpClient(nameof(TelegramNotifier), c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IPnlAlertNotifier, TelegramNotifier>();
builder.Services.AddHostedService<PnlAlertHostedService>();

// ---- Universe builder + bulk trigger template ---------------------------------------------
builder.Services.AddSingleton(sp => new UniverseBuilderService(
    sp.GetRequiredService<InstrumentMasterService>(),
    sp.GetRequiredService<ISpreadDefinitionStore>(),
    sp.GetRequiredService<TickIngestionHostedService>(),
    sp.GetRequiredService<ILogger<UniverseBuilderService>>(),
    universeCsvPath));
builder.Services.AddSingleton<BulkTriggerTemplateService>();

// ---- Authentication (JWT) -------------------------------------------------------------------
// Locks the entire API + SignalR hub behind a Bearer token. Only users listed in
// App_Data/users.csv can obtain a token (POST /api/auth/token); the external world can't consume
// anything without one. See JwtTokenService (mints) / the fallback policy below (enforces).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new CsvUserStore(usersCsvPath, sp.GetRequiredService<ILogger<CsvUserStore>>()));
builder.Services.AddSingleton<JwtTokenService>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // Browsers can't attach an Authorization header to a WebSocket handshake, so SignalR sends
        // the token as an ?access_token=... query-string param on the hub URL instead. Pull it out
        // for hub requests so the same JWT secures the real-time feed too.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

// Fallback policy = "authenticated user required" applied to EVERY endpoint that doesn't specify
// its own [Authorize]/[AllowAnonymous]. This is what makes the whole API secure-by-default:
// controllers, minimal-API endpoints, and the SignalR hub all require a valid token unless they
// explicitly opt out with [AllowAnonymous] (login, the Kite OAuth callback, and /healthz).
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---- Web / API / real-time ------------------------------------------------------------------
builder.Services.AddSignalR();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Lets the Swagger UI "Authorize" button take a Bearer token so endpoints can be tried in dev
    // now that everything requires one. Paste the token from POST /api/auth/token.
    var scheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter the JWT from POST /api/auth/token (just the token, no 'Bearer ' prefix).",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer",
        },
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

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

// Serves the built React dashboard (spreadtrader.dashboard.client's `npm run build` output,
// copied into wwwroot - see Dockerfile) from this same origin/process. The client calls the API
// with relative paths (/api/..., /hubs/spreads - see src/api.js and src/hooks/spreadHub.js), so
// serving both from one origin means no CORS is needed for the dashboard itself and there's only
// one URL/one deployable unit for "the app". Static files are plain middleware (not endpoints),
// so they're never subject to the JWT fallback policy below - the page shell always loads
// unauthenticated and the React app itself gates its UI behind the login screen.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(DashboardCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SpreadHub>("/hubs/spreads");
// Liveness probe only - no user data, must be reachable without a token by the Docker HEALTHCHECK
// / cloud probe, so it opts out of the global "authenticated user required" fallback policy.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// SPA fallback: any GET that isn't an API route, hub, or an existing static file falls through to
// index.html so client-side routing (react-router-dom) works on a hard refresh/deep link.
// AllowAnonymous is required - without it the global fallback policy above would demand a JWT just
// to load the page that lets you log in.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
