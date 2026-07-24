# SpreadTrader / LiveSpreadCalculator

Live calendar-spread dashboard for NSE/NFO futures (e.g. July vs August contracts). Works against
either **Zerodha Kite Connect** or **DhanHQ**, selected by a single configuration flag
(`Broker:Platform`) - see "Choosing a broker" below. Streams tick data over a WebSocket, computes each spread from the
legs' **best ask price** (not last traded price - see below), evaluates user-configured trigger
rules, and can punch basket orders automatically when a trigger fires. Ticks and spread values
live in process memory only and are lost on restart; trigger rules are persisted to a small CSV
file so they survive one (see Limitations).

## Projects

| Project | What it does |
|---|---|
| `SpreadTrader.Core` | Domain models and interfaces. No external dependencies. |
| `SpreadTrader.KiteIntegration` | Kite Connect login/token exchange, instrument master lookup, the binary WebSocket ticker client (parses best bid/ask from market depth), REST order placement, margin and positions. |
| `SpreadTrader.DhanIntegration` | The same, against DhanHQ API v2: consent/static-token login, scrip-master lookup, Dhan's binary market feed plus its separate order-update socket, order placement, margin and positions. |
| `SpreadTrader.Engine` | The background pipeline: tick ingestion -> spread calculation (ask-price based) -> trigger evaluation -> order dispatch. Runs as hosted services inside the Api process (not a separate process). References no broker project - it works purely against the interfaces in `SpreadTrader.Core/Brokers/`. |
| `LiveSpreadCalculator` | The ASP.NET Core Web Api. Hosts the REST endpoints, the SignalR hub that pushes live spread/trigger updates, and wires the whole DI graph together. **This is what you run.** |
| `SpreadTrader.Dashboard/spreadtrader.dashboard.client` | The React (Vite) dashboard - two pages: a read-only live grid (Dashboard) and configuration (Trigger Config: symbol sync, spreads, triggers). SignalR client. |

## Ask price, not last traded price

Trigger thresholds are evaluated against each leg's **best ask price** from live market depth,
not LTP - LTP can be stale between trades, ask price reflects what you'd actually pay right now.
This requires the ticker to run in Kite's `"full"` mode (`Kite:TickerMode` in `appsettings.json`,
already set for you) since only full-mode packets carry market depth; `"quote"`/`"ltp"` modes
don't have a bid/ask at all. Full mode costs more bandwidth/CPU than quote mode - fine at
~100-200 instruments, worth knowing if you scale up a lot further. The spread shown and
evaluated is `FarLeg.AskPrice - NearLeg.AskPrice`.

## Prerequisites

- .NET 8 SDK
- Node.js 18+ and npm
- A Kite Connect developer app (api_key + api_secret) from [developers.kite.trade](https://developers.kite.trade) - this is a paid Zerodha subscription, separate from your regular trading account
- Docker + Docker Compose, only if you want to run it containerized

## Configuration

All settings live under `LiveSpreadCalculator/appsettings.json`:

```json
"Broker": {
  "Platform": "Kite"
},
"Kite": {
  "ApiKey": "",
  "ApiSecret": "",
  "AccessToken": "",
  "WsUrl": "wss://ws.kite.trade",
  "RestBaseUrl": "https://api.kite.trade",
  "TickerMode": "full"
},
"Dhan": {
  "ClientId": "",
  "AccessToken": "",
  "AppId": "",
  "AppSecret": "",
  "RestBaseUrl": "https://api.dhan.co/v2",
  "AuthBaseUrl": "https://auth.dhan.co",
  "WsUrl": "wss://api-feed.dhan.co",
  "OrderUpdateWsUrl": "wss://api-order-update.dhan.co",
  "ScripMasterUrl": "https://images.dhan.co/api-data/api-scrip-master-detailed.csv",
  "TickerMode": "full",
  "OrdersPerSecond": 15,
  "AccessTokenValidityDays": 30
},
"Engine": {
  "SpreadRecalculationIntervalMs": 250,
  "MinFireIntervalPerRuleMs": 2000
},
"Dashboard": {
  "BaseUrl": "https://localhost:63061"
},
"Storage": {
  "TriggersCsvPath": "App_Data/triggers.csv",
  "FuturesCsvPath": "App_Data/futures_instruments.csv"
}
```

- **ApiKey / ApiSecret**: from your Kite Connect app. Register `https://localhost:7105/api/auth/callback` as the app's redirect URL for local dev.
- **AccessToken**: leave blank. It's obtained by completing the login flow (below) and is held in memory from then on - Kite invalidates it daily around 6 AM regardless of how it was issued.
- **TickerMode**: leave as `"full"` unless you have a specific reason not to - see "Ask price, not last traded price" above.
- **Dashboard.BaseUrl**: where the React app is served from. Used for CORS and for redirecting back after Kite login.
- **Storage paths**: relative to the Api project's content root. `TriggersCsvPath` is where trigger rules are persisted (rewritten on every add/remove/fire/enable/disable). `FuturesCsvPath` is where the futures symbol list gets written when you click "Sync symbols from broker" on the Trigger Config page. Both directories are created automatically if missing.
- If you'd rather not put ApiKey/ApiSecret in a file that might end up in source control, override them with environment variables instead (`Kite__ApiKey`, `Kite__ApiSecret`, `Dhan__ClientId`, `Dhan__AccessToken` - see Docker section) or `dotnet user-secrets`. Either way, keep real values out of git - and add `App_Data/` to `.gitignore` too, since the CSV files it holds aren't meant to be committed either.

## Choosing a broker

`Broker:Platform` decides which broker the whole app talks to - `"Kite"` (default) or `"Dhan"`.
Only that platform's config section is read; everything else in the app is identical either way.
Override it per-deployment with the `Broker__Platform` environment variable.

**Running on Dhan.** Two ways to authenticate:

1. *Static token (simplest).* Generate an access token in the Dhan web portal
   (Profile -> DhanHQ Trading APIs) and set `Dhan:ClientId` + `Dhan:AccessToken`. There is no
   browser login step at all - the app is authenticated the moment it starts, and the dashboard
   hides its login button. Set `AccessTokenValidityDays` to the validity you chose (up to 30).
2. *Consent flow (registered partner app).* Set `Dhan:AppId` + `Dhan:AppSecret` and leave
   `AccessToken` blank. The dashboard's login button then runs Dhan's browser consent handshake;
   register `<your-host>/api/auth/callback` as the app's redirect URL. These tokens last 24 hours.

If a Dhan token expires while the app is running, you don't need a redeploy: the dashboard shows
**Enter Dhan token**, which posts a freshly generated token to `POST /api/auth/broker-token`. It's
verified against Dhan before being accepted and takes effect immediately.

Two things to know before trading on Dhan:

- **Whitelist your outbound IP** with Dhan before order placement will work (data APIs work
  without it) - the same requirement `zerodha-ip-whitelist.txt` documents for Zerodha.
- **Rebuild the universe after switching.** Run `POST /api/universe/build` (or the "Build universe"
  button) once after changing platform and restarting. Kite and Dhan use different tradingsymbols
  and different instrument ids, so spreads resolved against one broker's instrument master can't be
  reused against the other's. Spread definitions are in-memory only, so this costs nothing.

See ARCHITECTURE.md §3.1 for the interfaces involved and what actually differs between the two.

## Running locally

Two things need to run at once - the Api and the dashboard.

**Terminal 1 - Api:**
```
cd LiveSpreadCalculator
dotnet run
```
Starts on `https://localhost:7105` (see `Properties/launchSettings.json`). Swagger UI is at `/swagger` in development.

**Terminal 2 - Dashboard:**
```
cd SpreadTrader.Dashboard/spreadtrader.dashboard.client
npm install
npm run dev
```
Starts on `https://localhost:63061` and proxies `/api/*` and `/hubs/*` to the Api (see `vite.config.js`) - open that URL in your browser, not the Api's.

**First-time login:** on Kite the dashboard shows "Zerodha Kite: not logged in" until you click **Login to Zerodha Kite**, complete Zerodha's login/2FA in the browser, and get redirected back. That obtains the access token for the day; you'll need to repeat it once every trading day, since Kite doesn't support long-lived tokens. On Dhan with a portal-generated token there is no login step at all - the app starts authenticated. (Either way, the dashboard caches the token client-side and offers it back after a backend restart, so a redeploy mid-day doesn't force a re-login.)

**Then, on the Trigger Config page:**
1. Click **Sync symbols from broker** - downloads every futures contract on NFO (options and equities excluded) and saves them to `App_Data/futures_instruments.csv`. Only needs to be done again when you want a fresh symbol list (new expiries listed, etc.) - not on every visit.
2. **Add spread** - pick an underlying, then near/far contracts from the dropdowns (auto-picks the two nearest expiries; override if you want a different pair).
3. **Add trigger** - pick the spread, an operator/threshold against the ask-price spread, which leg(s) to Buy/Sell when it fires, quantity, and whether it's one-shot or re-arms.

Both spreads and triggers take effect immediately, no restart needed. Switch to the **Dashboard** page to watch live values and fired triggers.

## Building for production

```
cd LiveSpreadCalculator
dotnet publish -c Release -o ./publish
```
```
cd SpreadTrader.Dashboard/spreadtrader.dashboard.client
npm run build      # outputs to ./dist
```
The dashboard build is static files - serve them from any static host or web server, as long as `/api/*` and `/hubs/*` are reverse-proxied to the Api (see `nginx.conf` for a working example).

## Hosting / deployment

### Docker (Api only)

```
cd LiveSpreadCalculator
docker build -t spreadtrader-api -f Dockerfile ..
docker run -p 8080:8080 \
  -e Kite__ApiKey=your_api_key \
  -e Kite__ApiSecret=your_api_secret \
  -e Dashboard__BaseUrl=https://your-dashboard-domain \
  spreadtrader-api
```
The Dockerfile builds `LiveSpreadCalculator` and its project references (`SpreadTrader.Core`, `SpreadTrader.KiteIntegration`, `SpreadTrader.DhanIntegration`, `SpreadTrader.Engine`) into one image. It exposes a `GET /healthz` endpoint and has a `HEALTHCHECK` wired to it. Never bake `ApiKey`/`ApiSecret` into the image - pass them as environment variables (or your platform's secrets manager) at run time, using ASP.NET Core's `Section__Key` env var convention.

### Docker Compose (Api + dashboard together)

```
docker compose up --build
```
Runs two containers - `api` (the backend, port 8080) and `web` (nginx serving the built React app on port 8081, reverse-proxying `/api` and `/hubs` to `api`). Set `KITE_API_KEY`, `KITE_API_SECRET`, and `DASHBOARD_BASE_URL` in your shell or a `.env` file next to `docker-compose.yml` before running - see that file for the exact variable names.

### Cloud deployment notes

- **Run the Api as a single instance.** The tick ingestion/trigger engine must be a singleton - both brokers cap concurrent feed connections per account (Kite at 3 per API key; Dhan disconnects with code 805 over the limit), and running two instances would double-evaluate triggers and could double-fire orders. Don't set replica count > 1 for this service.
- The **dashboard** (nginx/static) has no such constraint and can scale out normally if you ever need to.
- If you outgrow one process later, the Engine only depends on the abstractions in `SpreadTrader.Core` (`ITickStore`, `ISpreadPublisher`, `ITriggerEventSink`, `IOrderGateway`) - swap the SignalR-backed implementations for a message-queue-backed one and it can move to its own container without touching the Engine itself.

## Limitations to keep in mind

- **Ticks and spreads are not persisted** - a restart loses live prices and computed spread values (expected: they're only meaningful live). **Spread definitions are also in-memory only** - re-add them via the Trigger Config page after a restart (they're quick to re-add now that the symbol picker is synced). **Trigger rules are the one thing that persists**, via the CSV file described above - but see the next point.
- **The trigger CSV is a stopgap, not a database.** The whole file is rewritten on every add/remove/fire/enable/disable. Fine at the scale of tens or low hundreds of rules; would not hold up under high write concurrency. If that ever matters, swap `CsvTriggerRuleStore` for a real database behind the same `ITriggerRuleStore` interface - nothing else needs to change.
- **No official .NET SDK exists for either broker** - the WebSocket tick parsers and REST calls here are hand-implemented against each vendor's documented protocol, not a vendor library.
- **Daily login is required on Kite.** Kite access tokens expire ~6 AM daily by regulation; there's no way around re-authenticating once a day. Dhan tokens last 24 hours (consent flow) or up to 30 days (portal-generated), so this is a Kite-specific constraint.
- **Full ticker mode uses more bandwidth than quote mode** since it carries market depth on every change - necessary for ask-price triggers, but worth knowing if you scale well past ~200 instruments.
- **This places real orders.** SEBI's retail algo-trading framework (mandatory since April 2026) requires algo orders to carry an exchange-assigned Algo-ID tagged through your broker - confirm with Zerodha how a self-built strategy like this should be tagged before running it live.
