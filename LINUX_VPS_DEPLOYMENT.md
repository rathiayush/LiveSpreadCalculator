# Deploying SpreadTrader to a Linux VPS with Dokploy

The Linux/Dokploy counterpart of `DEPLOYMENT.md` (Azure) and `PLESK_DEPLOYMENT.md` (Windows/IIS via
Plesk). Target architecture:

```
GitHub                                  GitHub Actions
  .NET API                                Build images (api, web)
  React dashboard                         Run tests
  Dockerfiles          ──push──▶          Push to GHCR
  docker-compose.yml                      Trigger Dokploy deploy
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

Images are built via `docker compose build`/`push` against `docker-compose.yml` (its `api`/`web`
services' `image:` fields are driven by the `API_IMAGE`/`WEB_IMAGE` env vars - see the comments
there), so the compose file stays the single source of truth for build context/Dockerfile paths
whether you're running `docker compose up --build` locally or a workflow is building in CI. Each of
the three per-instance workflows pushes its **own tag**, not a shared `:latest` - so redeploying
Production can never be affected by whatever pradeep-1's last build happened to push, even though
the image content is identical today:

```
ghcr.io/<owner>/<repo>/api:production   (+ :production-<commit-sha>)
ghcr.io/<owner>/<repo>/api:pradeep-1    (+ :pradeep-1-<commit-sha>)
ghcr.io/<owner>/<repo>/api:pradeep-2    (+ :pradeep-2-<commit-sha>)
ghcr.io/<owner>/<repo>/web:production   (+ :production-<commit-sha>)
ghcr.io/<owner>/<repo>/web:pradeep-1    (+ :pradeep-1-<commit-sha>)
ghcr.io/<owner>/<repo>/web:pradeep-2    (+ :pradeep-2-<commit-sha>)
```

using each workflow's built-in `GITHUB_TOKEN` - no separate registry account needed. Two things to
set up once:

- **Package visibility.** If this repo is private (it is - see the repo's own settings), the GHCR
  packages default to private too. Dokploy needs a way to pull them: either make the packages public
  (GitHub → your profile → Packages → each package → Package settings → Change visibility) or give
  Dokploy a GHCR pull credential (a GitHub PAT with `read:packages`, added as a registry credential
  in Dokploy's settings). Public packages are simpler if you're not worried about the image contents
  being visible (they contain no secrets - see the Dockerfile's own comment about never baking
  `ApiKey`/`ApiSecret` into the image).
- **First push.** A tag doesn't exist until its workflow runs once - run each of the three manually
  (Actions tab → "Deploy to Dokploy - Production" / "- pradeep-1" / "- pradeep-2" → Run workflow)
  after setting up the repo secrets in §5.

---

## 3. Dokploy applications (six total: api + web, × three instances)

This doc assumes the standard Dokploy flow: one **Application** per deployable, each pulling a GHCR
image, with Dokploy managing Traefik/SSL for you through its **Domains** tab - no manual Traefik
labels needed. (If you'd rather deploy `docker-compose.yml` directly as a single Dokploy **Compose**
app instead, see §7 - the tradeoffs are different enough that it's kept separate rather than
complicating this section. Compose mode also doesn't map cleanly onto three independent instances
sharing one image but needing separate config, so §3's per-Application approach is the better fit
here regardless.)

Repeat this whole section three times, once per instance - substitute `production` / `pradeep-1` /
`pradeep-2` for the image tag and use that instance's own values throughout.

### 3a. `api` application

1. Dokploy dashboard → Create Application → **Docker Image** source (not git) →
   `ghcr.io/<owner>/<repo>/api:production` (or `:pradeep-1` / `:pradeep-2`).
2. **Environment variables** (Dokploy's Environment tab) - same `Section__Key` convention as every
   other deployment in this repo, and see §5 below for exactly which secrets you need to have ready:

   | Variable | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Production` / `pradeep-1` / `pradeep-2` - selects which `appsettings.*.json` loads |
   | `ASPNETCORE_URLS` | `http://+:8080` |
   | `Kite__ApiKey` / `Kite__ApiSecret` (or `Dhan__*` - check that instance's `Broker:Platform`) | **this instance's own** broker credentials - each of the three uses a different account, never share one across instances |
   | `Kite__AccessToken` | leave empty - set daily via the dashboard's "Login to Kite" |
   | `Dashboard__BaseUrl` | this instance's own public HTTPS URL, e.g. `https://spreadtrader.example.com`, `https://p1.spreadtrader.example.com` |
   | `Storage__SpreadHistoryConnectionString` | points at Postgres - see §4. All three instances can point at the SAME Postgres database - only the writer instance (`Engine:SpreadHistoryWriter=true`, currently pradeep-2 per `appsettings.pradeep-2.json`) writes to it |
   | `Storage__TriggersCsvPath` etc. | absolute paths under **this instance's own** mounted volume - see §6. Do NOT share one volume across instances, or they'll overwrite each other's trigger rules |

3. **Port**: container port `8080` (matches `ASPNETCORE_URLS` and the Dockerfile's `EXPOSE 8080`).
4. **Domain** (Dokploy's Domains tab): only if you want the API directly reachable on its own
   subdomain. If `web` reverse-proxies `/api` and `/hubs` to `api` the way it does in
   `docker-compose.yml` (see `nginx.conf.template` referenced in `DEPLOYMENT.md` §6), you may not
   need a public domain on `api` at all - Dokploy's internal Docker network lets `web` reach `api` by
   its Dokploy service name without exposing it publicly. Match whichever topology you actually run.
5. **Health check**: point it at `GET /healthz` (see `Program.cs` - `AllowAnonymous`, no auth
   needed). This is a liveness probe only, not the engine-health signal - see the note in
   `PLESK_DEPLOYMENT.md` §5 if you want a real "is the engine actually ticking" probe; that work
   hasn't been ported to this branch.

### 3b. `web` application

Same pattern per instance: Docker Image source `ghcr.io/<owner>/<repo>/web:production` (or
`:pradeep-1` / `:pradeep-2`), container port `80` (nginx), Domain = that instance's own public
domain with Dokploy-managed SSL.

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
string you can copy into `api`'s `Storage__SpreadHistoryConnectionString` environment variable
(§3a). This is the simplest path when `api`/`web` are separate Dokploy Applications rather than a
Compose deployment, and it puts Postgres under Dokploy's own backup/restart handling.

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

Set under the repo's Settings → Secrets and variables → Actions. These let the workflows push
images and ask Dokploy to redeploy - they are NOT read by the running app itself, so they don't need
to be broker credentials or anything trading-related:

| Secret | What it is | Shared or per-instance? |
|---|---|---|
| `DOKPLOY_URL` | Your Dokploy instance's base URL, e.g. `https://dokploy.example.com` | Shared (one Dokploy dashboard) |
| `DOKPLOY_API_TOKEN` | An API token generated in Dokploy (Settings → Profile, or wherever your version puts it) | Shared |
| `DOKPLOY_PRODUCTION_API_APP_ID` / `DOKPLOY_PRODUCTION_WEB_APP_ID` | Production's api/web Application IDs (visible in each Dokploy dashboard URL) | Per-instance |
| `DOKPLOY_PRADEEP1_API_APP_ID` / `DOKPLOY_PRADEEP1_WEB_APP_ID` | pradeep-1's, same way | Per-instance |
| `DOKPLOY_PRADEEP2_API_APP_ID` / `DOKPLOY_PRADEEP2_WEB_APP_ID` | pradeep-2's, same way | Per-instance |

8 secrets total. The application IDs don't exist until you've created the six Dokploy applications
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

### 5b. App runtime config (Dokploy environment variables, per instance - §3a's table)

**Yes, you need to provide Kite/Dhan credentials** - one set per instance, since each of the three
already uses its own separate broker account (confirmed against the committed `appsettings.*.json`
files - they're not interchangeable). These go into each `api` Application's environment variables
in Dokploy, never into GitHub Actions secrets and never into a file committed to git:

- `Kite__ApiKey` / `Kite__ApiSecret` (or `Dhan__ApiKey` / `Dhan__ApiSecret` etc. if that instance's
  `Broker:Platform` is `Dhan` - check `appsettings.<env>.json`) - this instance's real values.
- `Kite__AccessToken` - **do not** set this as a standing secret anywhere. It's obtained fresh each
  trading day via the dashboard's "Login to Kite" button (see `AuthController.Callback`) - setting a
  permanent one here would just be an expired token sitting in config, since Kite access tokens are
  single-day.
- `Storage__SpreadHistoryConnectionString` - the Postgres connection string from §4. Not secret in
  the same sense as broker keys, but still shouldn't be committed to git - same reasoning as
  `appsettings.*.json`'s connection-string placeholders (§0).

The Kite/Dhan values already sitting in the committed `appsettings.*.json` files are flagged in
`DEPLOYMENT.md` §0 as compromised (this repo's git history), so treat this migration as a good time
to actually rotate them in each broker's developer console, not just copy the old ones over.

---

## 6. Persistent state (App_Data)

Same hazard as every other deployment target in this repo: `triggers.csv`, `exit_triggers.csv`,
`pnl-alerts.csv`, `users.csv`, and the universe CSVs are runtime state rewritten by the app, not
static config - see the `.csproj` comment on why `App_Data\**` is bundled into every publish output
in the first place. A container's own filesystem does **not** survive a redeploy (a new image means a
new container, means a fresh filesystem), so without a persistent mount every deploy silently wipes
your trigger rules.

In Dokploy: add a **Volume** mount on the `api` Application (its Advanced/Volumes tab) - a host path
or Dokploy-managed volume mounted at, say, `/data`, then set the `Storage:*CsvPath` environment
variables to absolute paths under it:

```
Storage__TriggersCsvPath=/data/triggers.csv
Storage__ExitTriggersCsvPath=/data/exit_triggers.csv
Storage__PnlAlertsCsvPath=/data/pnl-alerts.csv
Storage__UsersCsvPath=/data/users.csv
Storage__UniverseCsvPath=/data/universe_futures.csv
Storage__McxUniverseCsvPath=/data/universe_mcx.csv
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
deployments (or one compose file parameterized per instance via a `.env`), which is more moving
parts than the per-Application approach in §3 for no real benefit here. Mentioned for completeness;
§3 is the better fit for this specific three-instance setup.

This needs changes §3 doesn't: Dokploy's Compose mode typically expects the `dokploy-network` to be
declared `external: true` so Traefik can reach your services, and you'd add Traefik routing labels
directly to the `api`/`web` service definitions (domain, entrypoint, TLS resolver) rather than using
the Domains-tab UI flow in §3. The exact label shape depends on your Dokploy/Traefik version, and
getting it wrong silently means "no public route" rather than a loud error - worth doing once,
carefully, against a test domain before pointing production DNS at it. Ask if you want this written
out concretely once you've decided against the per-Application approach in §3.

---

## 8. Also worth knowing

- **Each of the three instances must individually stay a single running copy** - see `README.md`'s
  "Cloud deployment notes". Both brokers cap concurrent feed connections per account, and the tick
  store / positions cache / rankings are in-memory and per-process, so scaling any ONE instance's
  `api` to more than one replica means that instance double-evaluates triggers and possibly
  double-fires orders. Running Production + pradeep-1 + pradeep-2 side by side is fine and intended
  (they're different broker accounts); replica count > 1 on any single one of them is not.
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
