# Deploying SpreadTrader to a Linux VPS with Dokploy

The Linux/Dokploy counterpart of `DEPLOYMENT.md` (Azure) and `PLESK_DEPLOYMENT.md` (Windows/IIS via
Plesk). Target architecture:

```
GitHub                                  GitHub Actions
  .NET API + React dashboard              Build image (api)
  Dockerfile                              Run tests
  docker-compose.yml   ──push──▶          Push to GHCR
                                           Trigger Dokploy deploy
                                                  │
                                                  ▼
                                          VPS (Dokploy)
                                            Docker
                                            PostgreSQL  (spread history)
                                            Traefik     (SSL + reverse proxy)
```

This replaces the Windows/IIS/SQL Server stack entirely: no app pool idle timeout to fight (see
`PLESK_DEPLOYMENT.md` §1 for why that mattered there), no named SQL Server instance, one systemd-free
deployment where the container *is* the process boundary - Docker restarts it, not IIS recycling it.

**One container per instance, not two.** `LiveSpreadCalculator/Dockerfile` builds the React dashboard
and bakes its static output into the same image's own `wwwroot`; `Program.cs` serves it from that
same origin via `UseStaticFiles`/`MapFallbackToFile`. So the `api` image already IS the whole
deployable - UI, REST API, and SignalR hub, one container, one port, no reverse-proxy hop between the
dashboard and the API it talks to. There is no separate "web"/nginx container in this setup (a
previous revision of this doc had one; `docker-compose.yml`'s comments explain why it was dropped).
If you specifically want a split API/UI topology instead - e.g. to scale the UI independently, or to
match the Azure sidecar model - `SpreadTrader.Dashboard/spreadtrader.dashboard.client/Dockerfile`
still exists for that (used by `DEPLOYMENT.md`'s Azure setup); it's just not wired into this doc or
`docker-compose.yml`.

**Three instances, like the existing Azure setup.** This repo already runs three independent
deployments of identical code - Production, pradeep-1, pradeep-2 (see `deploy.ps1`,
`deploy-pradeep-1.ps1`, `deploy-pradeep-2.ps1`, and `master_livespreadcalculator.yml` /
`master_p1-livespreadcalculator.yml` / `master_p2-livespreadcalculator.yml`). Each has its own Kite
or Dhan account and API key - genuinely different credentials, not a shared one (verified against
`appsettings.Production.json` / `.pradeep-1.json` / `.pradeep-2.json`). This doc carries that same
three-instance shape onto Dokploy: **one shared Docker image** (all three run identical code, told
apart purely by `ASPNETCORE_ENVIRONMENT` selecting which `appsettings.*.json` loads - see
`deploy-pradeep-1.ps1`'s header comment), but **three independent GitHub Actions workflows** and
**three independent Dokploy applications**, so redeploying one instance never touches another's
running container.

Read `DEPLOYMENT.md` §0 first if you haven't - `appsettings.json`'s committed Kite `ApiKey`/
`ApiSecret` are flagged there as compromised and due for rotation regardless of which platform you
deploy to.

---

## 0. Why Postgres, and what changed to get there

The engine's hourly spread-history table (`SpreadHistory` - see `EfSpreadHistoryStore`) used to run
on SQL Server, because the original deployment was Windows/Azure SQL. A Linux VPS has no SQL Server
option worth using, so this move ports that store to PostgreSQL via Npgsql - same EF Core model, same
`BulkInsertAsync`/`ExecuteDeleteAsync` shape, Postgres-native SQL types (`numeric`, `timestamptz`,
`varchar`) instead of SQL Server's (`decimal`, `datetimeoffset`, `nvarchar`). `Storage:*CsvPath`
(triggers, exit triggers, PNL alerts, universe, users) are untouched - those were always plain files,
not SQL Server.

If you're deploying fresh, there's nothing to migrate - Postgres creates its own empty schema on
first run (`EfSpreadHistoryStore.InitializeAsync`, called at startup on the writer instance). If
you're moving off an existing SQL Server deployment and want the accumulated history rows carried
over, that's a one-time export/import outside the scope of this doc - ask before doing it, since it
touches a live database.

---

## 1. One-time VPS setup

1. Provision the VPS (root access, a public IP, ideally a static one - if you use Kite, its IP
   whitelist depends on this staying fixed; see `zerodha-ip-whitelist.txt` for the pattern used on
   other deployments). A Hostinger Ubuntu VPS works fine here - Dokploy's installer targets Ubuntu
   directly, nothing provider-specific needed.
2. Point a DNS A record at the VPS IP for whatever domain(s) you'll use (e.g. `spreadtrader.example.com`).
3. Install Dokploy - follow [Dokploy's own install docs](https://docs.dokploy.com) for the current
   one-line installer; it brings up Docker, Traefik, and Dokploy's own dashboard for you. Not
   reproduced here since it changes across Dokploy releases and this doc would just go stale.
4. Log into the Dokploy dashboard (`https://<vps-ip>:3000` until you put a domain in front of it).

Memory/CPU planning: this is a handful of low-concurrency background timers (spread recalculation
every ~150ms across the live universe - see `EngineOptions.SpreadRecalculationIntervalMs`), not a
throughput-heavy web server, but Postgres alongside it on the same box needs real headroom. Don't run
this on the smallest tier VPS available - undersizing this specifically risks the recalculation loop
falling behind during market hours, which is a trigger-evaluation delay, not just a slow page load.

---

## 2. GitHub Container Registry (GHCR)

The image is built via `docker compose build`/`push` against `docker-compose.yml` (its `api`
service's `image:` field is driven by the `API_IMAGE` env var - see the comment there), so the
compose file stays the single source of truth for build context/Dockerfile path whether you're
running `docker compose up --build` locally or a workflow is building in CI. Each of the three
per-instance workflows pushes its **own tag**, not a shared `:latest` - so redeploying Production can
never be affected by whatever pradeep-1's last build happened to push, even though the image content
is identical today:

```
ghcr.io/<owner>/<repo>/api:production   (+ :production-<commit-sha>)
ghcr.io/<owner>/<repo>/api:pradeep-1    (+ :pradeep-1-<commit-sha>)
ghcr.io/<owner>/<repo>/api:pradeep-2    (+ :pradeep-2-<commit-sha>)
```

using each workflow's built-in `GITHUB_TOKEN` - no separate registry account needed. Two things to
set up once:

- **Package visibility.** If this repo is private (it is - see the repo's own settings), the GHCR
  package defaults to private too. Dokploy needs a way to pull it: either make the package public
  (GitHub → your profile → Packages → the package → Package settings → Change visibility) or give
  Dokploy a GHCR pull credential (a GitHub PAT with `read:packages`, added as a registry credential
  in Dokploy's settings). Public is simpler if you're not worried about the image contents being
  visible (they contain no secrets - see the Dockerfile's own comment about never baking
  `ApiKey`/`ApiSecret` into the image).
- **First push.** A tag doesn't exist until its workflow runs once - run each of the three manually
  (Actions tab → "Deploy to Dokploy - Production" / "- pradeep-1" / "- pradeep-2" → Run workflow)
  after setting up the repo secrets in §5.

---

## 3. Dokploy applications (three total - one per instance)

This doc assumes the standard Dokploy flow: one **Application** per instance, each pulling a GHCR
image, with Dokploy managing Traefik/SSL for you through its **Domains** tab - no manual Traefik
labels needed. (If you'd rather deploy `docker-compose.yml` directly as a Dokploy **Compose** app
instead, see §7 - kept separate since the tradeoffs are different enough to complicate this section.)

Repeat this section three times, once per instance - substitute `production` / `pradeep-1` /
`pradeep-2` for the image tag and use that instance's own values throughout.

1. Dokploy dashboard → Create Application → **Docker Image** source (not git) →
   `ghcr.io/<owner>/<repo>/api:production` (or `:pradeep-1` / `:pradeep-2`).
2. **Environment variables** (Dokploy's Environment tab). Every ASP.NET Core config key this app
   reads is listed below - **you don't need to set them one field at a time**: `.env.production.example`
   / `.env.pradeep-1.example` / `.env.pradeep-2.example` in the repo root already have every line
   below, named exactly as shown, ready to copy-paste as one block into Dokploy's environment editor
   (which accepts a raw `KEY=VALUE` block, not just individual fields). Copy that instance's file,
   fill in the blanks with real values, paste the whole thing in one shot. **Never paste real values
   into a chat with an AI assistant, including this one** - fill them in directly in your own editor.

   | Variable | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Production` / `pradeep-1` / `pradeep-2` - selects which `appsettings.*.json` loads |
   | `ASPNETCORE_URLS` | `http://+:8080` |
   | `Kite__ApiKey` / `Kite__ApiSecret` (Kite instances only) | **this instance's own** broker credentials - never share one across instances. pradeep-2 uses Kite; check `Broker:Platform` in that instance's `appsettings.*.json` if unsure |
   | `Kite__AccessToken` | leave empty - set daily via the dashboard's "Login to Kite", never a standing value |
   | `Dhan__ClientId` + (`Dhan__Pin`+`Dhan__TotpSecret` for TOTP mode, or `Dhan__AppId`+`Dhan__AppSecret` for Consent mode) (Dhan instances only) | **this instance's own** broker credentials - Production and pradeep-1 use Dhan, with different auth modes each; see that instance's `.env.*.example` for exactly which fields its `Dhan:AuthMode` needs |
   | `Jwt__Key` | signs this instance's auth tokens - see the ⚠️ in §5, this is currently a shared placeholder across all three instances in git and must become a real secret |
   | `Security__PasswordKey` | encrypts this instance's `App_Data/users.csv` - same ⚠️ as `Jwt__Key`. **Do not change this again once set and real users exist**, or their stored passwords become undecryptable |
   | `Telegram__BotToken` / `Telegram__ChatId` | optional order/PNL alerts - leave blank for no notifications |
   | `Dashboard__BaseUrl` | this instance's own public HTTPS URL - the SAME domain you put in the Domains tab below, since this one container serves both the API and the UI (e.g. `https://spreadtrader.example.com`, `https://p1.spreadtrader.example.com`) |
   | `Storage__SpreadHistoryConnectionString` | points at Postgres - see §4. All three instances can point at the SAME Postgres database - only the writer instance (`Engine:SpreadHistoryWriter=true`, currently pradeep-2 per `appsettings.pradeep-2.json`) writes to it |
   | `Storage__TriggersCsvPath` etc. (9 paths total - see §6) | absolute paths under **this instance's own** mounted volume - see §6. Do NOT share one volume across instances, or they'll overwrite each other's trigger rules |

3. **Port**: container port `8080` (matches `ASPNETCORE_URLS` and the Dockerfile's `EXPOSE 8080`) -
   this is the only port; the dashboard, `/api`, and `/hubs` are all served from it.
4. **Domain** (Dokploy's Domains tab): this instance's own public domain, routed straight to port
   `8080`. Must match `Dashboard__BaseUrl` above exactly.
5. **Health check**: point it at `GET /healthz` (see `Program.cs` - `AllowAnonymous`, no auth
   needed). This is a liveness probe only, not the engine-health signal - see the note in
   `PLESK_DEPLOYMENT.md` §5 if you want a real "is the engine actually ticking" probe; that work
   hasn't been ported to this branch.

### Redeploying

Dokploy redeploys an Application when you tell it to pull its configured tag again - either manually
from its dashboard, or via the API call that instance's GitHub Actions workflow makes (see §5).
Because each instance is pinned to its own tag (`:production`, not a shared `:latest`), redeploying
one instance never repoints another - it only ever pulls whatever that instance's own workflow last
pushed. Roll back a single instance by repointing its Dokploy application at its own
`:<env>-<commit-sha>` tag (every workflow run pushes one) instead of `:<env>`.

---

## 4. PostgreSQL

Two ways to run it - pick one, don't run both:

**Dokploy-managed Database (recommended for this setup).** Dokploy dashboard → Create Database →
PostgreSQL. Dokploy provisions the container, generates credentials, and gives you a connection
string you can copy into each instance's `Storage__SpreadHistoryConnectionString` environment
variable (§3). This is the simplest path when instances are separate Dokploy Applications rather than
a Compose deployment, and it puts Postgres under Dokploy's own backup/restart handling.

**The `db` service in `docker-compose.yml`.** Useful for local dev (`docker compose up`) and for the
Compose-deployment alternative in §7, where it becomes the same container Dokploy manages. Requires
`POSTGRES_PASSWORD` set (no default, by design - `docker compose up` fails fast rather than starting
Postgres with a guessable password) and its data lives in the named volume `spreadtrader-db-data`,
which survives `docker compose down` but not `docker compose down -v`.

Either way, `Storage__SpreadHistoryConnectionString` needs the Npgsql format:

```
Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<password>
```

The table (`SpreadHistory`, plus its indexes) is created automatically on first startup by whichever
instance has `Engine:SpreadHistoryWriter=true` (see `EfSpreadHistoryStore.InitializeAsync`) - nothing
to run by hand.

---

## 5. What you actually need to provide, and where

Two separate categories, going to two separate places. **Never paste real credentials into a chat
with an AI assistant, including this one** - set them directly in GitHub's/Dokploy's own UI.

### 5a. GitHub Actions secrets (build & deploy-trigger only)

Set under the repo's Settings → Secrets and variables → Actions. These let the workflows push the
image and ask Dokploy to redeploy - they are NOT read by the running app itself, so they don't need
to be broker credentials or anything trading-related:

| Secret | What it is | Shared or per-instance? |
|---|---|---|
| `DOKPLOY_URL` | Your Dokploy instance's base URL, e.g. `https://dokploy.example.com` | Shared (one Dokploy dashboard) |
| `DOKPLOY_API_TOKEN` | An API token generated in Dokploy (Settings → Profile, or wherever your version puts it) | Shared |
| `DOKPLOY_PRODUCTION_APP_ID` | Production's Application ID (visible in its Dokploy dashboard URL) | Per-instance |
| `DOKPLOY_PRADEEP1_APP_ID` | pradeep-1's, same way | Per-instance |
| `DOKPLOY_PRADEEP2_APP_ID` | pradeep-2's, same way | Per-instance |

5 secrets total. The application IDs don't exist until you've created the three Dokploy applications
in §3, so this is naturally the last step - create the applications first, then come back and set
these.

⚠️ **The workflows' Dokploy-trigger steps are marked UNVERIFIED in each workflow file itself** - I
could not test them against a live Dokploy instance from here, and Dokploy's REST API surface has
changed across versions. Before relying on this unattended: open your instance's Swagger/OpenAPI
docs, confirm the deploy endpoint's path/method/header/body match what's in the workflow, and run
each of the three once by hand to confirm the dashboard actually redeploys. The image
build-and-push-to-GHCR half has no such caveat - that part follows a standard, well-established
pattern I'm confident in.

All three `.github/workflows/deploy-dokploy-*.yml` are **manual-trigger only** (`workflow_dispatch`),
matching `deploy-azure.yml`'s existing, deliberate choice: a merge to master should not be able to
redeploy a live trading app on its own. Add a `push:` trigger yourself if you decide you want that.

### 5b. App runtime config (Dokploy environment variables, per instance - §3's table)

**Yes, you need to provide real values for all of these** - one full set per instance. Every field
below is already laid out, correctly named, in that instance's `.env.<env>.example` template (see
§3) - fill in the blanks there and paste the whole block into Dokploy once, rather than typing each
field individually. These go into each Application's environment variables in Dokploy, never into
GitHub Actions secrets and never into a file committed to git:

- **Broker credentials** - this instance's own account, never shared with another instance
  (confirmed against the committed `appsettings.*.json` files - genuinely different accounts, not
  interchangeable):
  - Kite instances (pradeep-2): `Kite__ApiKey` / `Kite__ApiSecret`.
  - Dhan instances (Production, pradeep-1): `Dhan__ClientId` plus whichever pair that instance's
    `Dhan:AuthMode` needs - `Dhan__Pin`+`Dhan__TotpSecret` for TOTP (fully automatic), or
    `Dhan__AppId`+`Dhan__AppSecret` for Consent (browser login, recurring). **Treat
    `Pin`/`TotpSecret` as login credentials, not tokens** - unlike a leaked access token (expires
    within 24h regardless of what happens to it), that pair lets anyone mint a fresh valid token
    indefinitely until you rotate it in the Dhan portal.
  - `Kite__AccessToken` - **never** set this as a standing secret anywhere, for any instance. It's
    obtained fresh each trading day via the dashboard's "Login to Kite" button (see
    `AuthController.Callback`) - a permanent one here would just be an expired token sitting in
    config, since Kite access tokens are single-day.

- ⚠️ **`Jwt__Key` and `Security__PasswordKey`** - **found sitting in git as the exact same
  `"CHANGE_ME_dev_only_..."` / `"CHANGE_ME_pwd_dev_only_..."` placeholder across all three
  instances' `appsettings.*.json` files, apparently never actually overridden.** `Jwt__Key` signs
  every auth token this instance issues - anyone who has that string can forge a valid login token
  for that instance's dashboard/API. `Security__PasswordKey` encrypts `App_Data/users.csv` - see
  `PasswordProtector`. Generate a real, random 32+ character value for each, ideally a **different**
  one per instance so a leak of one doesn't affect the others. **Do not change `Security__PasswordKey`
  again once it's set and real users exist** - every already-encrypted password becomes permanently
  undecryptable if you do (see `Tools/Decrypt-Users.ps1`'s own warning). This file did not touch
  these values in `appsettings.*.json` - rotating `Jwt__Key` logs everyone out (mild), but rotating
  `Security__PasswordKey` blind is destructive, so that has to be a deliberate action you take, not
  something done for you.

- `Telegram__BotToken` / `Telegram__ChatId` - optional (order/PNL alerts). Also currently committed
  in git as real, live bot tokens (different per instance, at least). Rotate via @BotFather if
  you're treating this migration as the point to clean these up; leave both blank in Dokploy for no
  notifications.

- `Storage__SpreadHistoryConnectionString` - the Postgres connection string from §4. Not secret in
  the same sense as broker keys, but still shouldn't be committed to git - same reasoning as
  `appsettings.*.json`'s connection-string placeholders (§0).

The broker/Telegram values already sitting in the committed `appsettings.*.json` files are exposed
in this repo's git history and should be treated as compromised (see `DEPLOYMENT.md` §0's warning
about the Kite key specifically - the same applies to everything else found here), so treat this
migration as a good time to actually rotate them in each provider's console, not just copy the old
ones over.

---

## 6. Persistent state (App_Data)

Same hazard as every other deployment target in this repo: `triggers.csv`, `exit_triggers.csv`,
`pnl-alerts.csv`, `users.csv`, the universe CSVs, and a few more (nine files total - see below) are
runtime state rewritten by the app, not static config - see the `.csproj` comment on why
`App_Data\**` is bundled into every publish output in the first place. A container's own filesystem
does **not** survive a redeploy (a new image means a new container, means a fresh filesystem), so
without a persistent mount every deploy silently wipes your trigger rules.

In Dokploy: add a **Volume** mount on the Application (its Advanced/Volumes tab) - a host path or
Dokploy-managed volume mounted at, say, `/data`, then set every one of these nine `Storage:*Path`
environment variables to an absolute path under it (all nine are already in the `.env.*.example`
files from §3 - `Storage__DhanScripMasterCachePath` only matters for the two Dhan instances, harmless
to set on pradeep-2 too if you'd rather keep all three identical):

```
Storage__TriggersCsvPath=/data/triggers.csv
Storage__ExitTriggersCsvPath=/data/exit_triggers.csv
Storage__PnlAlertsCsvPath=/data/pnl-alerts.csv
Storage__FuturesCsvPath=/data/futures_instruments.csv
Storage__DhanScripMasterCachePath=/data/dhan_scrip_master_cache.csv
Storage__BrokerPlatformOverridePath=/data/broker-platform-override.txt
Storage__UniverseCsvPath=/data/universe_futures.csv
Storage__McxUniverseCsvPath=/data/universe_mcx.csv
Storage__UsersCsvPath=/data/users.csv
```

This is the same fix `DEPLOYMENT.md` §4 does with an Azure Files share and `PLESK_DEPLOYMENT.md`
would do by moving these paths outside the FTP docroot - the mechanism differs per platform, the
underlying problem (state living inside a folder that gets discarded on redeploy) is the same
everywhere.

---

## 7. Alternative: deploy `docker-compose.yml` directly as a Dokploy Compose app

Instead of per-instance Applications (§3), Dokploy can run `docker-compose.yml` as-is via its
**Compose** application type - this also brings `db` (Postgres) along as part of the same deployment
rather than as a separate Dokploy Database resource. Note this doesn't map cleanly onto three
instances sharing one image with different config each - you'd need three separate compose
deployments (one per instance's own `.env.*` file - see `.env.production.example` /
`.env.pradeep-1.example` / `.env.pradeep-2.example`), which is more moving parts than the
per-Application approach in §3 for no real benefit here. Mentioned for completeness; §3 is the better
fit for this specific three-instance setup.

This needs changes §3 doesn't: Dokploy's Compose mode typically expects the `dokploy-network` to be
declared `external: true` so Traefik can reach your services, and you'd add a Traefik routing label
directly to the `api` service definition (domain, entrypoint, TLS resolver) rather than using the
Domains-tab UI flow in §3. The exact label shape depends on your Dokploy/Traefik version, and getting
it wrong silently means "no public route" rather than a loud error - worth doing once, carefully,
against a test domain before pointing production DNS at it. Ask if you want this written out
concretely once you've decided against the per-Application approach in §3.

---

## 8. Also worth knowing

- **Each of the three instances must individually stay a single running copy** - see `README.md`'s
  "Cloud deployment notes". Both brokers cap concurrent feed connections per account, and the tick
  store / positions cache / rankings are in-memory and per-process, so scaling any ONE instance to
  more than one replica means that instance double-evaluates triggers and possibly double-fires
  orders. Running Production + pradeep-1 + pradeep-2 side by side is fine and intended (they're
  different broker accounts); replica count > 1 on any single one of them is not.
- **Trading is live from the moment `Kite:AccessToken` exists** - no paper-trading mode exists in
  this codebase. Test trigger logic with thresholds that can't realistically fire before trusting it
  unattended, same warning as `DEPLOYMENT.md`.
- **Register the redirect URL** for EACH instance - `https://<that-instance's-domain>/api/auth/callback`
  in the [Kite Connect developer console](https://developers.kite.trade/apps) for that instance's own
  API key - and update the Kite IP whitelist with the VPS's IP (see `zerodha-ip-whitelist.txt` for
  the pattern; all three instances share one VPS IP now, unlike the current Azure setup where each
  had its own). Kite rejects the login callback and the market-data connection otherwise.
- **Logs**: `docker logs <container>` on the VPS, or Dokploy's own dashboard log viewer, is the
  fastest way to see why a deploy isn't coming up healthy - same role `az webapp log tail` plays in
  the Azure deployment.
