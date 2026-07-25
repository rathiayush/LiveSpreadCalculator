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
  "AuthMode": "Static",
  "ClientId": "",
  "AccessToken": "",
  "Pin": "",
  "TotpSecret": "",
  "AppId": "",
  "AppSecret": "",
  "RestBaseUrl": "https://api.dhan.co/v2",
  "AuthBaseUrl": "https://auth.dhan.co",
  "WsUrl": "wss://api-feed.dhan.co",
  "OrderUpdateWsUrl": "wss://api-order-update.dhan.co",
  "ScripMasterUrl": "https://images.dhan.co/api-data/api-scrip-master.csv",
  "TickerMode": "full",
  "OrdersPerSecond": 8,
  "AccessTokenValidityDays": 1
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
- **Storage paths**: relative to the Api project's content root. `TriggersCsvPath` is where trigger rules are persisted (rewritten on every add/remove/fire/enable/disable). `FuturesCsvPath` is where the futures symbol list gets written when you click "Sync symbols from broker" on the Trigger Config page. `DhanScripMasterCachePath` (Dhan only) is where the raw scrip master is saved after every successful download - it's what lets a restart warm the in-memory instrument index from disk instead of always re-downloading Dhan's ~26 MB dump first; see "Choosing a broker" below. `BrokerPlatformOverridePath` is where a broker switch made from the dashboard (Admin -> Broker Platform) is saved, taking precedence over `Broker:Platform`/`Broker__Platform` once written - see "Choosing a broker" below. All directories are created automatically if missing.
- If you'd rather not put ApiKey/ApiSecret in a file that might end up in source control, override them with environment variables instead (`Kite__ApiKey`, `Kite__ApiSecret`, `Dhan__ClientId`, `Dhan__AccessToken` - see Docker section) or `dotnet user-secrets`. Either way, keep real values out of git - and add `App_Data/` to `.gitignore` too, since the CSV files it holds aren't meant to be committed either.

## Choosing a broker

`Broker:Platform` decides which broker the app STARTS on - `"Kite"` (default) or `"Dhan"`. Override
it per-deployment with the `Broker__Platform` environment variable. Both platforms' services are
always constructed and running, regardless of this setting - it only picks which one is active the
moment the process boots (see "Switching from the dashboard" below for changing it afterwards).
Practical consequence: if you intend to ever switch to a platform, that platform's config section
should be filled in too (or a static/TOTP Dhan token authenticated, or a Kite login completed) -
otherwise switching to it works, but it shows up "not authenticated" until you do, exactly like an
unconfigured platform already behaves today.

**Switching from the dashboard.** Admin -> Broker Platform lets you switch between Kite and Dhan
instantly, with no restart and no downtime - both platforms' order gateways, market data feeds,
instrument masters, positions services and sessions exist side by side for the whole life of the
process (see `BrokerRegistration`); switching just flips which one every request is routed to (see
`BrokerSwitchState`). A platform that was already authenticated before the switch (e.g. Dhan on
TOTP mode, which keeps renewing its own token in the background regardless of whether Dhan is
currently active) is instantly usable - no re-login needed. The choice is also written to
`Storage:BrokerPlatformOverridePath` (a one-line file, `App_Data/broker-platform-override.txt` by
default), purely so a LATER, unrelated process restart (a redeploy, a crash) also comes back up on
whichever platform was last switched to, rather than reverting to `Broker:Platform`. Like any
platform switch, this clears every live tick and spread definition (trigger rules and PNL alerts
are untouched - they're saved to disk and keyed by a broker-neutral id, see below) and needs a
universe rebuild afterwards, since Kite and Dhan use different instrument ids for the same
contracts.

**Running on Dhan.** No browser login is required either way - pick between a token you paste in
yourself, or one the app generates for itself forever:

1. *Static token.* Generate an access token in the Dhan web portal (Profile -> DhanHQ Trading
   APIs) and set `Dhan:ClientId` + `Dhan:AccessToken`. The app is authenticated the moment it
   starts, and the dashboard hides its login button. **Dhan currently fixes portal tokens at
   24-hour validity** (there is no longer a longer-lived option), so this needs a fresh token
   pasted into the dashboard's "Enter Dhan token" box roughly once a day.
2. *TOTP (fully automatic).* Set `Dhan:AuthMode` to `"Totp"`, plus `Dhan:Pin` and
   `Dhan:TotpSecret`, and leave `AccessToken` blank. The app computes its own login codes and
   re-authenticates itself - at startup, about an hour before every 24-hour token expires, and
   immediately if a token is ever invalidated early - with no daily paste and no browser, ever.
   Recommended if you don't want a recurring manual step; see the security note below before
   choosing it.
3. *Consent flow (registered partner app).* Set `Dhan:AppId` + `Dhan:AppSecret` and leave
   `AccessToken` blank. The dashboard's login button then runs Dhan's browser consent handshake;
   register `<your-host>/api/auth/callback` as the app's redirect URL. These tokens last 24 hours.
   Independent of `AuthMode` - available as a manual fallback on top of either mode above.

**Security note on TOTP mode.** `Dhan:Pin` and `Dhan:TotpSecret` are login credentials, not
tokens - unlike a leaked access token (which is self-limiting; it expires within 24 hours
regardless), whoever holds both can mint a fresh valid token at will, indefinitely. Set them only
via an environment variable or `dotnet user-secrets`, never in a committed file, and rotate the
TOTP secret (Dhan Web -> Setup TOTP again) if you ever suspect it leaked.

If a Dhan token expires while the app is running and you're on Static mode, you don't need a
redeploy: the dashboard shows **Enter Dhan token**, which posts a freshly generated token to
`POST /api/auth/broker-token`. It's verified against Dhan before being accepted and takes effect
immediately. (On TOTP mode this button is unnecessary - the app has already re-authenticated
itself by the time you'd notice.)

Two things to know before trading on Dhan:

- **Whitelist your outbound IP** with Dhan before order placement will work (data APIs work
  without it) - the same requirement `zerodha-ip-whitelist.txt` documents for Zerodha.
- **Rebuild the universe after switching.** Run `POST /api/universe/build` (or the "Build universe"
  button) once after changing platform and restarting. Kite and Dhan use different tradingsymbols
  and different instrument ids, so spreads resolved against one broker's instrument master can't be
  reused against the other's. Spread definitions are in-memory only, so this costs nothing.
- **The Dhan scrip master refreshes once a day** (06:00 IST by default -
  `Engine:InstrumentMasterRefreshTimeIst`), not on every request - this app mostly trades monthly
  F&O contracts, which don't need intraday-fresh listings. The download is also saved to disk
  (`Storage:DhanScripMasterCachePath`), so a restart loads instantly from that local file instead
  of waiting on a fresh ~26 MB download from Dhan before anything can resolve a symbol.

### Every `Dhan:*` key

All fifteen keys, with the environment variable that overrides each one. `ClientId` is always
required; which of `AccessToken` / (`Pin` + `TotpSecret`) / (`AppId` + `AppSecret`) you also need
depends on `AuthMode` - see "Choosing a broker" above. Every other key already defaults to the
right value and is only there so a deployment can point at something else without a rebuild.

| Key | Env var | Default | What it does |
|---|---|---|---|
| `AuthMode` | `Dhan__AuthMode` | `Static` | `Static` (paste a token yourself) or `Totp` (the app generates and renews its own, forever - see below). |
| `ClientId` | `Dhan__ClientId` | *(blank)* | **Required in every mode.** Your Dhan account id, e.g. `1100123456`. Sent as the `client-id` header on every REST call, as `dhanClientId` in order payloads, and as `clientId` on the feed socket. |
| `AccessToken` | `Dhan__AccessToken` | *(blank)* | `AuthMode: Static` only. Token from the Dhan portal. **Secret.** |
| `Pin` | `Dhan__Pin` | *(blank)* | `AuthMode: Totp` only. Your Dhan account PIN - sent alongside the TOTP code on every auto-login call. **Secret - see the security note above.** |
| `TotpSecret` | `Dhan__TotpSecret` | *(blank)* | `AuthMode: Totp` only. The base32 seed from Dhan Web -> Profile -> DhanHQ Trading APIs -> Setup TOTP (the same string/QR code you'd otherwise scan into an authenticator app). **Secret - treat as a login credential, not a token; see the security note above.** |
| `AppId` | `Dhan__AppId` | *(blank)* | Partner app id. Set together with `AppSecret` to enable the browser consent login - independent of `AuthMode`. |
| `AppSecret` | `Dhan__AppSecret` | *(blank)* | Partner app secret. **Secret.** |
| `RestBaseUrl` | `Dhan__RestBaseUrl` | `https://api.dhan.co/v2` | Trading/data REST root. |
| `AuthBaseUrl` | `Dhan__AuthBaseUrl` | `https://auth.dhan.co` | Consent/login/TOTP host - a different host from the trading API. |
| `WsUrl` | `Dhan__WsUrl` | `wss://api-feed.dhan.co` | Live market feed socket. |
| `OrderUpdateWsUrl` | `Dhan__OrderUpdateWsUrl` | `wss://api-order-update.dhan.co` | Order-update socket. Dhan keeps this separate from the feed; Kite multiplexes the equivalent onto its ticker. |
| `ScripMasterUrl` | `Dhan__ScripMasterUrl` | `.../api-scrip-master.csv` | Instrument dump. Must be the **compact** list - the detailed one has no per-contract trading symbol column and loads zero instruments. |
| `TickerMode` | `Dhan__TickerMode` | `full` | `full`, `quote` or `ltp`. Leave on `full`: it is the only mode carrying best bid/ask, which spreads are evaluated against. |
| `OrdersPerSecond` | `Dhan__OrdersPerSecond` | `8` | Order pacing. Dhan's published limit is 10/sec and it counts reads too, so this keeps headroom. |
| `AccessTokenValidityDays` | `Dhan__AccessTokenValidityDays` | `1` | `AuthMode: Static` only - how long the portal token you generated is valid (Dhan currently issues these fixed at 24 hours), so the dashboard expires its cached copy at the right time. `Totp` and consent-flow tokens ignore this - both use Dhan's fixed 24-hour validity directly. |

### Step by step: running on Dhan

Steps 1-3 are the same either way; step 4 branches on which mode you picked.

1. **Whitelist your outbound IP.** Log in at [web.dhan.co](https://web.dhan.co) → Profile →
   *DhanHQ Trading APIs* and whitelist the deployment's outbound IP. Without this, data APIs work
   but every order placement is rejected. For a local run this is your home/office public IP; for
   Azure it is the Web App's outbound IP.
2. **Note your client id** on the same page (e.g. `1100123456`) - required in every mode.
3. **Point the app at Dhan.** In `LiveSpreadCalculator/appsettings.json` set:
   ```json
   "Broker": { "Platform": "Dhan" }
   ```
   or, better, set `Broker__Platform=Dhan` as an environment variable so the committed file stays
   on `Kite`.
4. **Get credentials and supply them as environment variables** rather than editing the file -
   pick one:

   **Static token:**
   ```bash
   export Broker__Platform=Dhan
   export Dhan__ClientId=1100123456
   export Dhan__AccessToken=eyJ0eXAiOi...
   ```
   Generate the access token on the same *DhanHQ Trading APIs* page - it's valid 24 hours, so
   you'll repeat this export (or use the dashboard's "Enter Dhan token" button) roughly once a day.

   **TOTP (fully automatic, recommended if you don't want the daily step above):**
   ```bash
   export Broker__Platform=Dhan
   export Dhan__AuthMode=Totp
   export Dhan__ClientId=1100123456
   export Dhan__Pin=123456
   export Dhan__TotpSecret=JBSWY3DPEHPK3PXP
   ```
   Get the TOTP secret from the same page → *Setup TOTP* (it shows you the same base32 string/QR
   code an authenticator app would use - copy the string rather than scanning the QR, since
   nothing here reads a QR code). Read the security note above before choosing this mode - the PIN
   and secret together are a standing login credential, not a rotating token.

   On Windows PowerShell use `$env:Dhan__ClientId = "1100123456"` etc. In Azure these go in the Web
   App's Application Settings. Note the **double** underscore in every `Dhan__*` name - that is how
   .NET maps a flat environment variable onto a nested config key.
5. **Run it** exactly as for Kite (see "Running locally" below). There is no login step either
   way - the log should show `Instrument master ready: 658 futures across [NFO]` and
   `Connected to Dhan market feed`; on TOTP mode it should additionally show
   `Dhan TOTP auto-login succeeded for client ...` within the first couple of seconds.
6. **Rebuild the universe once**: `POST /api/universe/build`, or the *Build universe* button.
   This is required after switching platforms - see above.

If startup logs `Dhan scrip master ... is missing required columns`, `ScripMasterUrl` is pointing
at the detailed CSV; set it to the compact one in the table above. If the feed logs a disconnect
with code `807`/`809`, the token has expired or been invalidated - on Static mode, generate a new
one and use the dashboard's **Enter Dhan token** button (no redeploy needed); on TOTP mode this
self-heals within seconds and needs no action, so if it persists check the logs for a TOTP
auto-login failure (wrong PIN/secret, or the deployment's clock is far enough out of sync to
invalidate the TOTP window).

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
  --restart unless-stopped \
  -e Kite__ApiKey=your_api_key \
  -e Kite__ApiSecret=your_api_secret \
  -e Dashboard__BaseUrl=https://your-dashboard-domain \
  spreadtrader-api
```
The Dockerfile builds `LiveSpreadCalculator` and its project references (`SpreadTrader.Core`, `SpreadTrader.KiteIntegration`, `SpreadTrader.DhanIntegration`, `SpreadTrader.Engine`) into one image. It exposes a `GET /healthz` endpoint and has a `HEALTHCHECK` wired to it. Never bake `ApiKey`/`ApiSecret` into the image - pass them as environment variables (or your platform's secrets manager) at run time, using ASP.NET Core's `Section__Key` env var convention. `--restart unless-stopped` is ordinary crash recovery for a process meant to run unattended for weeks/months - not needed for the broker-platform switch, which takes effect in-process with no restart at all (see "Choosing a broker" below).

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
- **Daily login is required on Kite, with no way around it.** Kite access tokens expire ~6 AM daily by regulation, and Kite offers no programmatic way to mint one - a human has to complete the browser flow every trading day. Dhan tokens also last 24 hours, but Dhan's TOTP mode (`Dhan:AuthMode: "Totp"`) lets this app generate and renew its own with zero manual steps, ever - see "Choosing a broker" above. That option doesn't exist on Kite.
- **Full ticker mode uses more bandwidth than quote mode** since it carries market depth on every change - necessary for ask-price triggers, but worth knowing if you scale well past ~200 instruments.
- **This places real orders.** SEBI's retail algo-trading framework (mandatory since April 2026) requires algo orders to carry an exchange-assigned Algo-ID tagged through your broker - confirm with Zerodha how a self-built strategy like this should be tagged before running it live.
