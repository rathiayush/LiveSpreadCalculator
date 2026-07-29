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
   other deployments).
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

Images are pushed by `.github/workflows/deploy-dokploy.yml` to:

```
ghcr.io/<owner>/<repo>/api:latest   (+ :<commit-sha>)
ghcr.io/<owner>/<repo>/web:latest   (+ :<commit-sha>)
```

using the workflow's built-in `GITHUB_TOKEN` - no separate registry account needed. Two things to
set up once:

- **Package visibility.** If this repo is private (it is - see the repo's own settings), the GHCR
  packages default to private too. Dokploy needs a way to pull them: either make the packages public
  (GitHub → your profile → Packages → each package → Package settings → Change visibility) or give
  Dokploy a GHCR pull credential (a GitHub PAT with `read:packages`, added as a registry credential
  in Dokploy's settings). Public packages are simpler if you're not worried about the image contents
  being visible (they contain no secrets - see the Dockerfile's own comment about never baking
  `ApiKey`/`ApiSecret` into the image).
- **First push.** The package doesn't exist until the workflow runs once - run it manually
  (Actions tab → "Deploy to Dokploy (VPS)" → Run workflow) after setting up the repo secrets in §5.

---

## 3. Dokploy applications (api + web)

This doc assumes the standard Dokploy flow: one **Application** per deployable, each pulling a GHCR
image, with Dokploy managing Traefik/SSL for you through its **Domains** tab - no manual Traefik
labels needed. (If you'd rather deploy `docker-compose.yml` directly as a single Dokploy **Compose**
app instead of two separate Applications, see §7 - the tradeoffs are different enough that it's kept
separate rather than complicating this section.)

### 3a. `api` application

1. Dokploy dashboard → Create Application → **Docker Image** source (not git) →
   `ghcr.io/<owner>/<repo>/api:latest`.
2. **Environment variables** (Dokploy's Environment tab) - same `Section__Key` convention as every
   other deployment in this repo:

   | Variable | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `ASPNETCORE_URLS` | `http://+:8080` |
   | `Kite__ApiKey` / `Kite__ApiSecret` | your real values - never in git |
   | `Kite__AccessToken` | leave empty - set daily via the dashboard's "Login to Kite" |
   | `Dashboard__BaseUrl` | the public HTTPS URL you'll put in Dokploy's Domains tab, e.g. `https://spreadtrader.example.com` |
   | `Storage__SpreadHistoryConnectionString` | points at Postgres - see §4 |
   | `Storage__TriggersCsvPath` etc. | absolute paths under a mounted volume - see §6 |

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

Same pattern: Docker Image source, `ghcr.io/<owner>/<repo>/web:latest`, container port `80` (nginx),
Domain = your real public domain with Dokploy-managed SSL.

### Redeploying

Dokploy redeploys an Application when you tell it to pull the image again - either manually from its
dashboard, or via the API call the GitHub Actions workflow makes (see §5). Since both api and web are
tagged `:latest` on every push, "redeploy" always means "pull whatever the last build produced" -
keep the `:<sha>` tags around (the workflow pushes both) if you need to roll back to a specific one.

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

## 5. GitHub Actions secrets

Set these under the repo's Settings → Secrets and variables → Actions, for
`.github/workflows/deploy-dokploy.yml` to use:

| Secret | What it is |
|---|---|
| `DOKPLOY_URL` | Your Dokploy instance's base URL, e.g. `https://dokploy.example.com` |
| `DOKPLOY_API_TOKEN` | An API token generated in Dokploy (Settings → Profile, or wherever your version puts it) |
| `DOKPLOY_API_APPLICATION_ID` | The `api` Application's ID (visible in its Dokploy dashboard URL) |
| `DOKPLOY_WEB_APPLICATION_ID` | The `web` Application's ID, same way |

⚠️ **The workflow's Dokploy-trigger steps are marked UNVERIFIED in the workflow file itself** - I
could not test them against a live Dokploy instance from here, and Dokploy's REST API surface has
changed across versions. Before relying on this unattended: open your instance's Swagger/OpenAPI
docs, confirm the deploy endpoint's path/method/header/body match what's in the workflow, and run it
once by hand to confirm the dashboard actually redeploys. The image build-and-push-to-GHCR half of
the workflow has no such caveat - that part follows a standard, well-established pattern.

`.github/workflows/deploy-dokploy.yml` is **manual-trigger only** (`workflow_dispatch`), matching
`deploy-azure.yml`'s existing, deliberate choice: a merge to master should not be able to redeploy a
live trading app on its own. Add a `push:` trigger yourself if you decide you want that.

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

Instead of two separate Applications (§3), Dokploy can run `docker-compose.yml` as-is via its
**Compose** application type - this also brings `db` (Postgres) along as part of the same deployment
rather than as a separate Dokploy Database resource.

This needs changes §3 doesn't: Dokploy's Compose mode typically expects the `dokploy-network` to be
declared `external: true` so Traefik can reach your services, and you'd add Traefik routing labels
directly to the `api`/`web` service definitions (domain, entrypoint, TLS resolver) rather than using
the Domains-tab UI flow in §3. The exact label shape depends on your Dokploy/Traefik version, and
getting it wrong silently means "no public route" rather than a loud error - worth doing once,
carefully, against a test domain before pointing production DNS at it. Ask if you want this written
out concretely once you've decided against the per-Application approach in §3.

---

## 8. Also worth knowing

- **Run `api` as a single instance**, same as every other deployment target - see
  `README.md`'s "Cloud deployment notes". Both brokers cap concurrent feed connections per account,
  and the tick store / positions cache / rankings are in-memory and per-process, so a second instance
  means double-evaluated triggers and possibly double-fired orders, not extra capacity.
- **Trading is live from the moment `Kite:AccessToken` exists** - no paper-trading mode exists in
  this codebase. Test trigger logic with thresholds that can't realistically fire before trusting it
  unattended, same warning as `DEPLOYMENT.md`.
- **Register the redirect URL** `https://<your-domain>/api/auth/callback` in the
  [Kite Connect developer console](https://developers.kite.trade/apps) for your API key, and update
  the Kite IP whitelist with the VPS's IP (see `zerodha-ip-whitelist.txt` for the pattern) - Kite
  rejects the login callback and the market-data connection otherwise.
- **Logs**: `docker logs <container>` on the VPS, or Dokploy's own dashboard log viewer, is the
  fastest way to see why a deploy isn't coming up healthy - same role `az webapp log tail` plays in
  the Azure deployment.
