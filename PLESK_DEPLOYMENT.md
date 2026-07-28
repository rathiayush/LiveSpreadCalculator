# Deploying SpreadTrader on Plesk (and why the background services need care)

This is the Plesk counterpart to `DEPLOYMENT.md` (Azure). Read the first section before choosing a
hosting option — the default Plesk/IIS behaviour will silently stop this app's trading engine, and
the fix is a deployment decision, not a code setting.

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

The app serves the built React dashboard from its own `wwwroot` (`UseDefaultFiles` /
`UseStaticFiles` + the SPA fallback), so Plesk does not need to serve any files itself — it only has
to proxy. That keeps one deployable unit and avoids CORS entirely.

### 2a. Windows (Plesk for Windows / IIS)

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

Only worth doing if option A isn't available to you. It requires IIS Manager or `appcmd` access on
the server: `idleTimeout`, `startMode` and recycling live in **`applicationHost.config`, not in your
app's `web.config`**, so they cannot be set from the deployed application. On a shared Plesk plan
without server-level access, this option is simply not available — use option A.

On the domain's **dedicated** application pool (Plesk → domain → IIS Application Pool → "Dedicated
application pool for the website"):

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

## 6. Also worth knowing

- **Trading is live the moment a broker access token exists.** There is no paper-trading mode; see
  the same warning in `DEPLOYMENT.md`. Test with thresholds that cannot realistically fire.
- **`Dashboard:BaseUrl` must be the real public URL** — it's the broker's OAuth redirect target and
  what the CORS policy allows. Behind the Plesk proxy that's your domain, not `127.0.0.1:8080`.
- **Register the redirect URL** `https://<your-domain>/api/auth/callback` in the broker's developer
  console, or the login callback is rejected.
- **Never put real secrets in `appsettings.json`.** Use `dotnet user-secrets` locally, and
  environment variables or `appsettings.Production.json` outside the repo on the server.
