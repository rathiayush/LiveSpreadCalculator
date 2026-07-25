using System.Text;
using System.Text.Json.Serialization;
using LiveSpreadCalculator.Auth;
using LiveSpreadCalculator.Brokers;
using LiveSpreadCalculator.Hubs;
using LiveSpreadCalculator.Notifications;
using LiveSpreadCalculator.Persistence;
using LiveSpreadCalculator.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Realtime;
using SpreadTrader.Core.Store;
using SpreadTrader.Engine;
using SpreadTrader.Engine.Services;

var builder = WebApplication.CreateBuilder(args);

const string DashboardCorsPolicy = "Dashboard";

builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.SectionName));

// MCX commodity arbitrage add-on (silver/gold/crude/natgas calendar + cross-contract spreads).
// The whole feature is gated behind Mcx:Enabled (default false) - see McxOptions. Bound once here
// and also registered as a plain singleton so both DI-constructed services (OrderDispatch, the
// UniverseController) and the hand-wired singletons below (instrument master, positions, universe
// builder) read the exact same resolved config. With Enabled=false every consumer is a no-op and
// the NFO engine is unchanged.
builder.Services.Configure<McxOptions>(builder.Configuration.GetSection(McxOptions.SectionName));
var mcxOptions = builder.Configuration.GetSection(McxOptions.SectionName).Get<McxOptions>() ?? new McxOptions();
builder.Services.AddSingleton(mcxOptions);

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
// Dhan only: the raw scrip master exactly as downloaded (not the filtered futures list above),
// persisted to disk so a process restart can warm its in-memory instrument index from the local
// file instead of always re-downloading Dhan's ~26 MB dump first - see
// DhanInstrumentMasterService.EnsureLoadedAsync's doc comment. Unused on Kite.
var dhanScripMasterCachePath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:DhanScripMasterCachePath"] ?? "App_Data/dhan_scrip_master_cache.csv");
// The universe builder pulls its symbol list live from the Kite instrument master (every stock
// currently listed for NFO futures - see InstrumentMasterService.GetAllFuturesUnderlyings) rather
// than a hand-maintained CSV. How many symbols get built is configurable per-request via
// POST /api/universe/build?maxSymbols=N; this is just where the resolved output gets written -
// see UniverseBuilderService.
var universeCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:UniverseCsvPath"] ?? "App_Data/universe_futures.csv");
// Separate output file for the MCX commodity universe so building it never overwrites the NFO
// universe CSV above - only written when the MCX add-on is enabled and built.
var mcxUniverseCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:McxUniverseCsvPath"] ?? "App_Data/universe_mcx.csv");
// Allow-list of who may log in and obtain a JWT (email,password rows). See CsvUserStore.
var usersCsvPath = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:UsersCsvPath"] ?? "App_Data/users.csv");

// Hourly spread history lives in a real SQL Server / LocalDB database (unlike the CSV stores above),
// so the "Live Spread" popup can chart datetime-vs-spread and a leg's rows can be flushed on expiry -
// see SqlSpreadHistoryStore / SpreadHistoryCaptureHostedService. Default targets LocalDB; override
// Storage:SpreadHistoryConnectionString (or Storage__SpreadHistoryConnectionString env var) with a
// real SQL Server connection string on Linux/Docker, where LocalDB isn't available.
var spreadHistoryConnectionString = builder.Configuration["Storage:SpreadHistoryConnectionString"]
    ?? @"Server=(localdb)\MSSQLLocalDB;Database=SpreadTraderHistory;Trusted_Connection=True;TrustServerCertificate=True;";
// Exactly one deployed instance (pradeep-2) is the WRITER of the shared history DB: only it captures
// snapshots, creates the schema, and runs purges. Every other instance leaves Engine:SpreadHistoryWriter
// false and only READS the history (to serve the chart), so multiple instances on one database never
// double-write or race. Read at startup here to decide whether to register the capture service and run
// schema init below.
var spreadHistoryWriter = builder.Configuration.GetValue<bool>("Engine:SpreadHistoryWriter");

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
// Durable hourly spread history (EF Core over SQL Server / LocalDB) - see EfSpreadHistoryStore.
// A pooled DbContext factory is used because the store (and the capture hosted service that drives
// it) are singletons, and a DbContext must not be shared across threads - the store creates a
// short-lived context per operation instead. Schema is created on startup below
// (app.Services...InitializeAsync): the database if missing, then the table if missing.
builder.Services.AddDbContextFactory<SpreadHistoryDbContext>(options =>
    options.UseSqlServer(spreadHistoryConnectionString));
builder.Services.AddSingleton<ISpreadHistoryStore>(sp =>
    new EfSpreadHistoryStore(
        sp.GetRequiredService<IDbContextFactory<SpreadHistoryDbContext>>(),
        spreadHistoryConnectionString,
        sp.GetRequiredService<ILogger<EfSpreadHistoryStore>>()));
// Rolling in-memory history of fired triggers/execution outcomes, so GET /api/triggers/activity
// can reseed the Trigger Activity feed after a browser refresh - see TriggerActivityEntry.
builder.Services.AddSingleton<ITriggerActivityStore, InMemoryTriggerActivityStore>();

// ---- Broker integration ---------------------------------------------------------------------
// Registers exactly one platform's implementations - Kite or Dhan - behind the interfaces in
// SpreadTrader.Core.Brokers, chosen by the Broker:Platform setting. Everything below this line
// (engine, controllers, hub) is written against those interfaces and is identical either way.
// See BrokerRegistration for the full list of what gets swapped.
builder.Services.AddBrokerIntegration(builder.Configuration, mcxOptions, futuresCsvPath, dhanScripMasterCachePath);

// Refreshes the instrument master (Dhan's ~26 MB scrip master, or Kite's per-exchange dump) on a
// background timer well inside its own 12h cache window, so no live request - the Add Spread
// pickers, GET /api/positions, POST /api/spreads - is ever the one that pays for a stale-cache
// reload. See InstrumentMasterRefreshHostedService's doc comment for the full reasoning.
builder.Services.AddHostedService<InstrumentMasterRefreshHostedService>();

// Shared MARKET -> protected-LIMIT conversion (brokers reject raw MARKET orders for F&O - see
// MarketProtectionService's doc comment). Used by both OrderDispatchHostedService (triggered
// baskets, registered below) and PositionsController (manual square-off).
builder.Services.AddSingleton<MarketProtectionService>();

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
// Hourly (IST :29-mark) snapshot capture into the SQL history DB + monthly expired-leg flush - see
// SpreadHistoryCaptureHostedService / EngineOptions.SpreadHistory*. ONLY registered on the writer
// instance; reader instances never capture (they only serve GET /api/spreads/{id}/history).
if (spreadHistoryWriter)
    builder.Services.AddHostedService<SpreadHistoryCaptureHostedService>();
// Daily 06:00 IST maintenance: clears entry/exit triggers + trigger activity and trims spread
// history older than the retention window - see DailyMaintenanceHostedService / EngineOptions.DailyMaintenance*.
builder.Services.AddHostedService<DailyMaintenanceHostedService>();

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
    sp.GetRequiredService<IBrokerInstrumentMaster>(),
    sp.GetRequiredService<ISpreadDefinitionStore>(),
    sp.GetRequiredService<TickIngestionHostedService>(),
    sp.GetRequiredService<ILogger<UniverseBuilderService>>(),
    universeCsvPath,
    mcxOptions,
    mcxUniverseCsvPath));
builder.Services.AddSingleton<BulkTriggerTemplateService>();

// ---- Authentication (JWT) -------------------------------------------------------------------
// Locks the entire API + SignalR hub behind a Bearer token. Only users listed in
// App_Data/users.csv can obtain a token (POST /api/auth/token); the external world can't consume
// anything without one. See JwtTokenService (mints) / the fallback policy below (enforces).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// Reversible, key-based password protection for App_Data/users.csv. The key comes from
// Security:PasswordKey, falling back to the JWT key so the app still boots if only that is set.
// NOTE: this must be the SAME value the PowerShell decrypt tool (Tools/Decrypt-Users.ps1) is given,
// or previously encrypted passwords can't be read back. Changing it makes existing enc: rows
// undecryptable.
var passwordKey = builder.Configuration["Security:PasswordKey"];
if (string.IsNullOrWhiteSpace(passwordKey))
    passwordKey = builder.Configuration["Jwt:Key"];
builder.Services.AddSingleton(new PasswordProtector(passwordKey ?? string.Empty));

builder.Services.AddSingleton(sp =>
    new CsvUserStore(usersCsvPath, sp.GetRequiredService<PasswordProtector>(), sp.GetRequiredService<ILogger<CsvUserStore>>()));
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
            },

            // Force-logout enforcement. Every request carries the security stamp the token was
            // issued with (the "stamp" claim); we compare it to the user's CURRENT stamp derived
            // from App_Data/users.csv. A password change rewrites that row, so its stamp no longer
            // matches any previously issued token - those tokens fail here (401), the client clears
            // its session, and the user is bounced to the login screen on every device. Tokens
            // minted before this feature shipped have no stamp claim and are likewise rejected,
            // requiring a one-time re-login after deploy.
            OnTokenValidated = context =>
            {
                var store = context.HttpContext.RequestServices.GetRequiredService<CsvUserStore>();
                var email = context.Principal?.Identity?.Name;
                var tokenStamp = context.Principal?.FindFirst("stamp")?.Value;
                var currentStamp = store.GetSecurityStamp(email);

                if (string.IsNullOrEmpty(tokenStamp) ||
                    string.IsNullOrEmpty(currentStamp) ||
                    !string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal))
                {
                    context.Fail("Security stamp mismatch - password was changed or the token is stale.");
                }

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

// One-time-per-file upgrade of App_Data/users.csv: any legacy plaintext password rows are rewritten
// as encrypted blobs on startup. No-op once every row is already encrypted, so ordinary restarts
// don't touch the file (and don't disturb live sessions). See CsvUserStore.MigrateToEncrypted.
app.Services.GetRequiredService<CsvUserStore>().MigrateToEncrypted();

// Create the spread-history database + table if they don't exist yet (idempotent). Only the WRITER
// instance provisions the schema - reader instances assume the writer has already created it and just
// query it (they may not even have CREATE DATABASE rights on a shared SQL login). Done at startup
// rather than lazily on first insert so a bad connection string surfaces loudly here instead of
// silently swallowing hourly captures. A failure is logged but not fatal - the rest of the app (live
// spreads, triggers, orders) runs fine without history; only the chart/capture is affected.
if (spreadHistoryWriter)
{
    try
    {
        app.Services.GetRequiredService<ISpreadHistoryStore>().InitializeAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Spread history store failed to initialize - hourly capture and the history chart will be unavailable until the connection string / SQL Server is fixed.");
    }
}

// Swagger has no auth of its own (it's plain middleware, registered before UseAuthentication/
// UseAuthorization below - not an endpoint, so the JWT fallback policy never applies to it) -
// normally gated to IsDevelopment() so it's never reachable on a real deployed instance
// (Production, or the actual pradeep-1/pradeep-2 Azure Web Apps). Swagger:ForceEnabled lets a
// LOCAL run under a non-Development environment name (e.g. ASPNETCORE_ENVIRONMENT=pradeep-2, so
// that instance's own appsettings/App_Data/users file loads for local testing) still get Swagger,
// without turning it on for the real deployed Azure instance of the same name - see the
// "pradeep-2 (local)" launch profile in launchSettings.json, which sets this env var; the actual
// Azure Web App does not.
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Swagger:ForceEnabled"))
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
