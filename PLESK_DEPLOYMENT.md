# Deploying SpreadTrader on Plesk (and why the background services need care)

This is the Plesk counterpart to `DEPLOYMENT.md` (Azure). Read the first section before choosing a
hosting option — the default Plesk/IIS behaviour will silently stop this app's trading engine, and
the fix is a deployment decision, not a code setting.

> **This deployment: a Windows VPS running Plesk, with administrator (RDP) access.**
> That means **section 2 is the path** — register the engine as a Windows Service and let Plesk
> reverse-proxy to it. Section 3 (tuning the IIS application pool) is a fallback for when you
> can't register a service, kept for reference; it is strictly weaker and you don't need it.
> Section 6 is the ordered checklist to work through.

---

## 1. The problem: this app is not a normal website

Everything the engine does runs as `IHostedService` background loops *inside the same process that
serves HTTP* — see the registrations in `LiveSpreadCalculator/Program.cs`:

| Service | What stops when the process stops |
|---|---|
| `TickIngestionHostedService` | The single broker ticker WebSocket, owned "for the life of the process". No ticks. |
| `SpreadCalculationHostedService` | Spread recalculation (~150 ms) and trigger evaluation. |
| `OrderDispatchHostedService` | **Entry and exit orders. Exit triggers on open positions stop firing.** |
| `DailyMaintenanceHostedService` | The 06:00 IST reset (clears trigger rules, trims history). |
| `PositionsCache` / `LivePositionsHostedService` | Position + PNL polling. |
| `PnlAlertHostedService` | Telegram PNL alerts. |
| `SpreadHistoryCaptureHostedService` | The hourly :29 history snapshots. |
| `InstrumentMasterRefreshHostedService` / `AutoUniverseBuilderHostedService` | Daily instrument refresh, universe build. |

A web host is entitled to stop that process whenever it likes, because as far as it knows this is a
website with nobody on it. IIS — which is what Plesk for Windows hosts ASP.NET Core through — does
exactly that, out of the box:

- **Idle Time-out, 20 minutes.** No HTTP requests for 20 minutes ⇒ the worker process is terminated.
- **Regular Time Interval recycle, 1740 minutes (29 h).** A rolling restart that drifts across the
  clock and will eventually land inside market hours.
- **Start Mode `OnDemand`.** After a server reboot, nothing runs until someone requests the site.

So: yes — **background services do go idle without traffic, and here that means the trading engine
stops.** Concretely, if nobody has the dashboard open, then 20 minutes later the feed is gone and
exit triggers are no longer being evaluated against your open positions; and the 06:00 IST
maintenance job never runs at all, because nobody is browsing the site at 6 am.

The failure is silent. When you next open the dashboard the process starts, reconnects and looks
perfectly healthy — with no indication that it was dead all afternoon. That is what
`GET /healthz/engine` (section 5) exists to catch.

---

## 2. Option A — run the engine as an OS service, proxy through Plesk (recommended)

Take process lifetime away from the web host and give it to the operating system. This is the same
shape as the Azure deployment (`web` container + `api` sidecar) and the local `docker-compose.yml`:
one long-lived engine process, with the web tier in front of it.

`Program.cs` calls `UseWindowsService()` / `UseSystemd()`, so the published build already runs as a
service with no code changes — both calls are inert when the process isn't started by that service
manager, so IIS, `dotnet run` and the Docker image are unaffected.

### There is no "run as a service" setting, by design

One build already covers every case, and it needs no configuration: hosting mode is **detected, not
configured**. `WindowsServiceHelpers.IsWindowsService()` asks whether this process was started by the
Windows Service Control Manager, and the same publish output therefore behaves correctly whether the
SCM starts it, IIS starts it, or you run it from a console.

An `appsettings.json` switch for this would be actively harmful, because it could only ever disagree
with reality:

- Set it to "Windows Service" but launch the exe by hand, and it blocks waiting for an SCM handshake
  that never arrives.
- Set it to "web" but let the SCM start it, and the SCM kills it for never reporting `SERVICE_RUNNING`
  inside the startup timeout.

A service isn't a mode the application picks; it's how the OS starts the process. What *is*
configurable is everything around it — the listening address (`--urls`), the environment
(`--environment Production`, selecting `appsettings.Production.json`), and anything else in config.

To see which mode a running deployment actually picked, check the startup log:

```
info: Hosting mode: Windows Service. Content root: C:\SpreadTrader\app. Environment: Production.
```

Content root is on that line deliberately — every `App_Data` path is resolved from it, so this one
line answers both "is it really running as a service" and "where is it reading trigger rules from".

### How the dashboard stays reachable

The engine binds to loopback, so the obvious question is how anyone reaches the UI. The answer is
that **the app serves the dashboard itself** — the built React client is copied into `wwwroot` by
`deploy.ps1`, and `Program.cs` serves it with `UseDefaultFiles()` / `UseStaticFiles()` plus
`MapFallbackToFile("index.html")` for client-side routing. One process serves the UI, the REST API
and the SignalR hub, exactly as it already does in Docker. Plesk becomes a pure reverse proxy:

```
Browser
  └─→ https://your-domain.com                 Plesk/IIS on :443 — TLS + public hostname
        └─→ proxies every path (ARR)
              └─→ http://127.0.0.1:8080       the Windows Service (Kestrel)
                    /              → wwwroot/index.html   (React dashboard)
                    /api/*         → controllers
                    /hubs/spreads  → SignalR
                    /healthz*      → probes
```

So the UI stays at the same public URL it is on today — nothing changes for whoever uses it. Plesk
serves no files of its own, which keeps one deployable unit and avoids CORS entirely.

Two consequences worth being deliberate about:

- **Binding to `127.0.0.1` is intentional.** The service is not reachable from the internet directly;
  Plesk is the only way in, so there's one TLS endpoint and one hostname. On the server itself,
  `curl http://127.0.0.1:8080/healthz` still works for testing.
- **Nothing starts the app on demand any more.** Under IIS, a request would start it; under a
  service, if the service is stopped Plesk returns 502 and the page won't load at all. That is the
  trade being made — it can't be started by traffic, which is precisely why it can't be stopped by
  the absence of it. `StartupType Automatic` plus the `sc.exe failure` restart actions below are what
  keep it up.

### 2a. Windows (Plesk for Windows / IIS) — the path for this deployment

Publish anywhere outside the Plesk vhost tree, e.g. `C:\SpreadTrader\app` (the existing
`deploy.ps1` publish step produces exactly this output):

```powershell
dotnet publish -c Release -o C:\SpreadTrader\app
```

Register it as a service, bound to loopback only — the public entry point is Plesk:

```powershell
New-Service -Name SpreadTrader `
  -BinaryPathName '"C:\SpreadTrader\app\LiveSpreadCalculator.exe" --urls http://127.0.0.1:8080 --environment Production' `
  -DisplayName "SpreadTrader Engine" `
  -Description "LiveSpreadCalculator trading engine + API" `
  -StartupType Automatic

# Restart on crash instead of staying down (5s, then 10s, then 30s; counter resets daily).
sc.exe failure SpreadTrader reset= 86400 actions= restart/5000/restart/10000/restart/30000

Start-Service SpreadTrader
```

Notes:

- `--urls` and `--environment` are ordinary command-line configuration, read by
  `WebApplication.CreateBuilder(args)`. `Production` makes it load `appsettings.Production.json`.
- The service runs as `LocalSystem` by default, which can write `App_Data`. If your SQL connection
  string uses integrated auth, run it as an account that SQL Server accepts instead
  (`New-Service -Credential`).
- `App_Data` is resolved relative to the binary's folder, not the service's working directory — the
  `ContentRootPath` pin at the top of `Program.cs` is what guarantees that. Without it the Windows
  SCM's `C:\Windows\System32` working directory would send every CSV path there.

Then point the Plesk domain at it. This needs **URL Rewrite** and **Application Request Routing**
installed, with ARR's server-level "Enable proxy" turned on, and WebSockets enabled (SignalR needs
them). Put this in the domain's `httpdocs\web.config`:

```xml
<configuration>
  <system.webServer>
    <webSocket enabled="true" />
    <rewrite>
      <rules>
        <rule name="SpreadTraderProxy" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://127.0.0.1:8080/{R:1}" />
          <serverVariables>
            <set name="HTTP_X_FORWARDED_PROTO" value="https" />
          </serverVariables>
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
```

### 2b. Linux (Plesk for Linux)

Plesk for Linux has no native ASP.NET Core hosting, so a systemd unit plus an nginx proxy is the
supported shape (the Plesk Docker extension is the other option, in which case use the existing
`LiveSpreadCalculator/Dockerfile` and skip to section 5).

`/etc/systemd/system/spreadtrader.service`:

```ini
[Unit]
Description=SpreadTrader Engine
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/spreadtrader
ExecStart=/usr/bin/dotnet /opt/spreadtrader/LiveSpreadCalculator.dll --urls http://127.0.0.1:8080
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=spreadtrader
User=spreadtrader
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now spreadtrader
sudo systemctl status spreadtrader
```

The `spreadtrader` user needs write access to `/opt/spreadtrader/App_Data` (trigger rules, PNL
alerts and the universe CSVs are rewritten at runtime).

Then in Plesk → the domain → **Apache & nginx Settings**, turn *off* "Proxy mode" and add under
*Additional nginx directives*:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600s;
    proxy_buffering off;
}
```

`proxy_read_timeout` and the `Upgrade`/`Connection` headers are what keep the SignalR WebSocket from
being cut every 60 seconds; `proxy_buffering off` keeps live pushes from being held back.

---

## 3. Option B — keep it under IIS, and stop IIS from killing it

**Fallback only.** This is for deployments without administrator access to the server — a shared
Plesk plan, where you cannot register a service. On a VPS you own, use option A instead: everything
below is a set of web-host settings that a service simply doesn't have, and that Plesk can reset out
from under you when a service plan or subscription is reapplied.

It is still worth doing as an interim measure if the site is live on IIS today and the service
isn't set up yet — the two zeros below remove most of the exposure in about a minute.

None of these settings live in your app's `web.config` — they're application-pool configuration in
`applicationHost.config`, so they cannot be set from the deployed application. Plesk does expose the
most important ones itself, though, under **domain → Dedicated IIS Application Pool → Performance
Settings** (no server-level access needed):

| Plesk setting | Set to | Why |
|---|---|---|
| Idle timeout (minutes) | **`0`** | 0 = never idle out. **Plesk's default here is 5 minutes**, not IIS's usual 20 — five quiet minutes and the engine is gone. |
| Recycling at regular time intervals (minutes) | **`0`** | Disables the drifting 1740-minute (29 h) restart. |
| The maximum number of worker processes | **`1`** | See the web-garden warning below. This is normally already 1 — never raise it. |

Two settings that matter are **not** on that page and still need IIS Manager or `appcmd`:
`startMode` / `preloadEnabled` (without them the pool stays down after a server reboot until the
first request) and `disallowOverlappingRotation`. Also note Plesk can reset pool settings when a
service plan or subscription is reapplied, so this configuration is not necessarily permanent — one
more reason to prefer option A.

Via `appcmd`, on the domain's **dedicated** application pool (Plesk → domain → IIS Application Pool
→ "Dedicated application pool for the website"):

```bat
set AC=%windir%\system32\inetsrv\appcmd
set POOL=<your app pool name>

REM Never stop for being idle.
%AC% set apppool /apppool.name:"%POOL%" /processModel.idleTimeout:00:00:00

REM Kill the drifting 29-hour recycle; recycle at a fixed, safe time instead (04:00 IST here -
REM before the 06:00 maintenance job and well before the 09:15 open).
%AC% set apppool /apppool.name:"%POOL%" /recycling.periodicRestart.time:00:00:00
%AC% set apppool /apppool.name:"%POOL%" /+recycling.periodicRestart.schedule.[value='04:00:00']

REM Start at boot without waiting for a request.
%AC% set apppool /apppool.name:"%POOL%" /startMode:AlwaysRunning
%AC% set site /site.name:"<your site>" /[path='/'].applicationDefaults.preloadEnabled:true

REM See the two warnings below - both of these matter more than they look.
%AC% set apppool /apppool.name:"%POOL%" /recycling.disallowOverlappingRotation:true
%AC% set apppool /apppool.name:"%POOL%" /processModel.maxProcesses:1
```

`preloadEnabled` needs the **Application Initialization** IIS role feature installed.

> ⚠️ **`disallowOverlappingRotation:true` is not optional here.** IIS's default overlapped recycle
> starts the replacement worker process *before* stopping the old one. For a few seconds you then
> have two engines: two broker WebSockets, two `OrderDispatchHostedService` instances consuming
> triggers, two of everything — able to place duplicate live orders.

> ⚠️ **`maxProcesses` must stay 1.** A web garden (>1 worker process) means N copies of the engine
> placing real orders off the same rules, permanently. Same reason `DEPLOYMENT.md` says not to scale
> the Azure app past a single instance: the tick store, positions cache and ranking are all in-memory
> and per-process.

### Keep-alive ping

With `idleTimeout:0` you don't need one. If you cannot change the pool settings, a Plesk **Scheduled
Task** every 5 minutes is the fallback:

```
curl -fsS --max-time 20 http://127.0.0.1:8080/healthz
```

Be clear about what this is: a band-aid. It does not stop the periodic recycle, it does not survive
the ping itself quietly failing, and it only proves the *web host* is up.

---

## 4. What a restart costs you

Whichever option you pick, know what is lost when the process does restart (a deploy, a crash, the
04:00 recycle):

- **Lost** — the tick store, the positions cache, spread rankings, the trigger activity feed: all
  in-memory by design.
- **Kept** — trigger rules, exit trigger rules, PNL alerts and the universe/futures lists, via the
  CSVs in `App_Data`; and spread history, in SQL.

`App_Data` must therefore live on real persistent disk and must be writable by the service account.
This is the Plesk equivalent of the Azure Files mount in `DEPLOYMENT.md` §4.

---

## 5. Monitoring — the part that makes the rest trustworthy

There are two probes, and they answer different questions:

| Endpoint | Question | Use it for |
|---|---|---|
| `GET /healthz` | Is the process up and serving HTTP? | Docker `HEALTHCHECK`, restart-on-failure supervisors |
| `GET /healthz/engine` | Is the engine actually working? | **Your uptime monitor / alerting** |

`/healthz` deliberately stays a plain liveness probe returning `{"status":"ok"}` — it must not go red
on a bad feed minute, or the container `HEALTHCHECK` would restart the process and throw away all
in-memory state without fixing anything upstream.

`/healthz/engine` reports whether tick ingestion is still running and how long ago the last tick
arrived, and returns **HTTP 503** when the engine isn't doing its job, so a monitor alerts without
having to parse the body:

```json
{
  "status": "unhealthy",
  "reason": "No tick has ever been received while 442 instrument(s) are subscribed and ticks are expected.",
  "uptime": "0.00:00:10",
  "feed": {
    "state": "running", "error": null, "subscribedTokens": 442,
    "lastTickUtc": null, "lastTickAgeSeconds": null, "ticksReceived": 0
  },
  "market": { "openIst": "09:15", "closeIst": "15:30", "isOpen": false, "ticksExpected": true }
}
```

Statuses: `ok` (200), `degraded` (503, ingestion running but ticks have gone stale), `unhealthy`
(503, ingestion stopped or faulted, or never produced a tick when it should have).

Staleness is only judged when ticks are genuinely expected — inside market hours, with at least one
instrument subscribed — so a quiet feed overnight is reported as `ok` rather than paging you every
night until you mute the alert. Tune via `Engine:*` in `appsettings.json`:

| Setting | Default | |
|---|---|---|
| `HealthMaxTickAgeSeconds` | `60` | How stale the last tick may get before `degraded` |
| `HealthCheckMarketHoursOnly` | `true` | Only judge staleness inside the window below |
| `HealthMarketOpenIst` / `HealthMarketCloseIst` | `09:15` / `15:30` | The window, IST |
| `HealthWeekdaysOnly` | `true` | Don't expect ticks Sat/Sun |

Two caveats worth knowing before you alert on it:

- **Exchange holidays are not modelled** (there's no holiday calendar in this app), so a mid-week
  holiday will report a stale feed.
- **If you run the MCX add-on** (`Mcx:Enabled`), widen `HealthMarketCloseIst` to that session's close
  (23:30 / 23:55 depending on the season) or the evening commodity session looks idle to the check
  while it is trading normally.

Point an external monitor (UptimeRobot, Better Stack, Plesk's own Health Monitoring, …) at
`https://<your-domain>/healthz/engine` and alert on non-200. External matters: a check running on the
same box goes down with the box.

The endpoint is anonymous by necessity — a monitor has no JWT — and exposes only timings, counts and
a feed state: no positions, orders, prices or user data. If you would still rather not have it
publicly reachable, drop `/healthz/engine` from the proxy rules in section 2 and monitor it from the
server itself over loopback (accepting the caveat above).

---

## 6. Checklist for this deployment (Windows VPS + Plesk, admin access)

In order. Steps 1-4 are the cutover; 5-7 are what make it trustworthy afterwards.

1. **Publish** outside the Plesk vhost tree — `dotnet publish -c Release -o C:\SpreadTrader\app`
   (§2a). Keep it off the drive Plesk manages so a panel operation can never touch it.
2. **Move `App_Data` with it** and confirm the service account can write there — trigger rules, exit
   triggers, PNL alerts and the universe CSVs are rewritten at runtime (§4). Copy the *live server*
   copies, not the repo's; they are current state, not config.
3. **Register the service** with `New-Service` + `sc.exe failure` restart actions, bound to
   `http://127.0.0.1:8080`, `--environment Production` (§2a). `Start-Service`, then verify with
   `curl http://127.0.0.1:8080/healthz` on the box.
4. **Point Plesk at it** — install URL Rewrite + ARR, enable the server-level proxy and WebSockets,
   drop the `web.config` from §2a into the domain's `httpdocs`. Load the site: the dashboard should
   come up on your normal public URL, and the top bar should read "Live feed: connected".
5. **Turn the old IIS site's app pool off** (or leave it stopped) once traffic is going through the
   proxy — two copies of this app running at once means two engines placing orders. If you keep it
   around as a rollback, keep it *stopped*, not idle.
6. **Set `Dashboard:BaseUrl`** to the public HTTPS URL and confirm the broker's redirect URL is
   registered in its developer console (§7), otherwise login fails at the callback.
7. **Point an external monitor at `https://<your-domain>/healthz/engine`** and alert on non-200
   (§5). Then verify the whole thing end to end: stop the service and confirm the monitor fires.
   An untested alert is not an alert.

A useful drill before trusting it unattended: `Restart-Service SpreadTrader` during market hours and
watch `/healthz/engine` go 503 and back to 200 on its own. That exercises the restart path, the
persistence of `App_Data`, and your alerting in one go.

---

## 7. Also worth knowing

- **Trading is live the moment a broker access token exists.** There is no paper-trading mode; see
  the same warning in `DEPLOYMENT.md`. Test with thresholds that cannot realistically fire.
- **`Dashboard:BaseUrl` must be the real public URL** — it's the broker's OAuth redirect target and
  what the CORS policy allows. Behind the Plesk proxy that's your domain, not `127.0.0.1:8080`.
- **Register the redirect URL** `https://<your-domain>/api/auth/callback` in the broker's developer
  console, or the login callback is rejected.
- **Never put real secrets in `appsettings.json`.** Use `dotnet user-secrets` locally, and
  environment variables or `appsettings.Production.json` outside the repo on the server.
