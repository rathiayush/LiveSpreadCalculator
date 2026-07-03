# Deploying SpreadTrader to Azure

This guide deploys the app as it's actually built: two containers behind one public endpoint -
`web` (nginx + the built React app, reverse-proxying `/api` and `/hubs` to the API) and `api`
(the `LiveSpreadCalculator` project - REST endpoints, SignalR hub, and the background engine that
recalculates spreads and dispatches orders). Locally that's `docker-compose.yml`. On Azure it's
one App Service running `web` as the main container with `api` as a **sidecar container** -
Azure's current, supported way to run more than one container behind a single App Service. (The
older "Docker Compose on App Service" feature does the same job but is being retired on
**March 31, 2027**, so it isn't used here even though it would look simpler today.)

`SpreadTrader.Dashboard.Server` / `spreadtrader.dashboard.client.esproj` are not part of this -
they're an earlier scaffold that predates the two-container setup and aren't referenced by
`docker-compose.yml` or either `Dockerfile`. Ignore them for deployment purposes.

---

## 0. Before anything else: rotate the Kite API secret

`LiveSpreadCalculator/appsettings.json` had a real Kite Connect `ApiKey`/`ApiSecret` committed in
plain text (now blanked out as part of this change). If that file has ever been pushed to a git
remote - public or private - **treat that secret as compromised**: regenerate it from the
[Kite Connect developer console](https://developers.kite.trade/apps) before deploying anything.
Deploying with a leaked secret just moves the exposure to production.

Going forward, real values only ever go into: local `dotnet user-secrets` (dev), GitHub Actions
secrets (CI), and Azure App Service Application Settings (runtime). Never back into
`appsettings.json`.

---

## 1. One-time Azure account setup

```bash
az login
az account list --output table          # confirm you're on the right subscription
az account set --subscription "<subscription-id-or-name>"
```

Pick names and a region up front - used throughout:

```bash
RG=spreadtrader-rg
LOCATION=centralindia          # pick whatever's closest to you / your Kite feed
ACR_NAME=spreadtraderacr$RANDOM   # must be globally unique, lowercase alphanumeric
PLAN=spreadtrader-plan
APP=spreadtrader-app$RANDOM       # becomes https://$APP.azurewebsites.net - must be globally unique
STORAGE=spreadtraderst$RANDOM
SHARE=spreadtrader-data
```

```bash
az group create --name $RG --location $LOCATION
```

---

## 2. Azure Container Registry (ACR)

```bash
az acr create --resource-group $RG --name $ACR_NAME --sku Basic --admin-enabled true
```

`--admin-enabled true` is the simplest way to get App Service pulling images for a first deploy.
Switch to a managed identity + `AcrPull` role assignment later if you want to drop the stored
password.

---

## 3. Build and push both images

From the repo root (`docker-compose.yml`'s directory):

```bash
az acr build --registry $ACR_NAME --image spreadtrader/api:latest \
  --file LiveSpreadCalculator/Dockerfile .

az acr build --registry $ACR_NAME --image spreadtrader/web:latest \
  --file SpreadTrader.Dashboard/spreadtrader.dashboard.client/Dockerfile \
  SpreadTrader.Dashboard/spreadtrader.dashboard.client
```

`az acr build` builds in Azure (no local Docker required) and pushes straight to your registry.
The web image's build context is the client folder itself (matching how `docker-compose.yml`
builds it), not the repo root.

---

## 4. Persistent storage for App_Data

`CsvTriggerRuleStore` rewrites `App_Data/triggers.csv` on every trigger change and reloads it on
startup, specifically so trigger rules survive a restart (see the comment at the top of that
file). A container's own filesystem does **not** survive being redeployed or restarted, so without
this step every deploy silently wipes your trigger rules (and the synced futures/universe CSVs).

```bash
az storage account create --resource-group $RG --name $STORAGE --sku Standard_LRS
az storage share create --account-name $STORAGE --name $SHARE

KEY=$(az storage account keys list --resource-group $RG --account-name $STORAGE --query "[0].value" -o tsv)
```

Mount it once the Web App exists (step 6) at `/data` on the `api` sidecar - not `/` or `/home`,
which Azure doesn't support as BYOS mount targets.

---

## 5. App Service plan (Linux)

```bash
az appservice plan create --resource-group $RG --name $PLAN --is-linux --sku B1
```

`B1` (Basic) is the cheapest tier that supports custom containers + sidecars and won't idle out
like Free/Shared tiers do - reasonable for a low-traffic personal dashboard. Scale up later if
needed.

---

## 6. Create the Web App (main container = `web`) and add `api` as a sidecar

```bash
ACR_LOGIN_SERVER=$(az acr show --name $ACR_NAME --query loginServer -o tsv)
ACR_USER=$(az acr credential show --name $ACR_NAME --query username -o tsv)
ACR_PASS=$(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv)

az webapp create --resource-group $RG --plan $PLAN --name $APP \
  --deployment-container-image-name $ACR_LOGIN_SERVER/spreadtrader/web:latest

az webapp config container set --resource-group $RG --name $APP \
  --container-registry-url https://$ACR_LOGIN_SERVER \
  --container-registry-user $ACR_USER \
  --container-registry-password $ACR_PASS

# Main container's public port - nginx listens on 80.
az webapp config appsettings set --resource-group $RG --name $APP \
  --settings WEBSITES_PORT=80

# api as a sidecar - internal only, reachable from the web container via localhost:8080
# (sidecars share one network namespace with the main container - see nginx.conf.template).
az webapp sitecontainers create --resource-group $RG --name $APP \
  --container-name api \
  --image $ACR_LOGIN_SERVER/spreadtrader/api:latest \
  --target-port 8080 \
  --registry-user $ACR_USER \
  --registry-password $ACR_PASS
```

`az webapp sitecontainers` is a newer CLI surface (sidecars went GA in late 2024) - run
`az webapp sitecontainers create --help` to confirm the exact flags on your installed CLI version
before running this; names occasionally shift between CLI releases.

Mount the Azure Files share from step 4 onto the `api` sidecar:

```bash
az webapp config storage-account add --resource-group $RG --name $APP \
  --custom-id spreadtrader-data \
  --storage-type AzureFiles \
  --account-name $STORAGE \
  --share-name $SHARE \
  --access-key "$KEY" \
  --mount-path /data
```

---

## 7. App settings (secrets, config, and the networking switch)

```bash
APP_URL="https://$APP.azurewebsites.net"

az webapp config appsettings set --resource-group $RG --name $APP --settings \
  API_UPSTREAM_HOST=localhost \
  Kite__ApiKey="<your-kite-api-key>" \
  Kite__ApiSecret="<your-kite-api-secret>" \
  Kite__AccessToken="" \
  Dashboard__BaseUrl="$APP_URL" \
  Storage__TriggersCsvPath="/data/triggers.csv" \
  Storage__FuturesCsvPath="/data/futures_instruments.csv" \
  Storage__UniverseCsvPath="/data/universe_futures.csv"
```

- `API_UPSTREAM_HOST=localhost` is what makes nginx (the main container) find the `api` sidecar -
  see `nginx.conf.template`. Without it nginx falls back to its Docker-Compose default (`api`),
  which doesn't resolve on App Service and every `/api`/`/hubs` request will fail.
- `Storage__*CsvPath` as **absolute** paths under `/data` (the mount from step 4) is what actually
  gets the CSVs onto persistent storage - `Program.cs` combines these with the app's content root,
  and `Path.Combine` returns an absolute second argument unchanged, so no code change was needed
  for this, just these three settings.
- `Dashboard__BaseUrl` must be the app's real public URL - it's where Kite redirects the browser
  after login (see `AuthController.Callback`) and what the CORS policy allows.
- Leave `Kite__AccessToken` empty; you'll get one per trading day via the "Login to Kite" button
  in the dashboard itself, not by setting it here.

**Register the redirect URL** `https://$APP.azurewebsites.net/api/auth/callback` in the
[Kite Connect developer console](https://developers.kite.trade/apps) for your API key - Kite
rejects the login callback otherwise.

Restart to pick everything up:

```bash
az webapp restart --resource-group $RG --name $APP
```

Then check `https://$APP.azurewebsites.net` loads, and `https://$APP.azurewebsites.net/healthz`
... actually `/healthz` is served by the `api` sidecar, which isn't publicly reachable by design
(only the main `web` container is) - check the dashboard itself loads and shows "Live feed:
connected" in the top bar instead. That's the real end-to-end signal: page load (web container) +
REST calls succeeding (nginx -> api) + SignalR connecting (nginx -> api over WebSockets).

---

## 8. GitHub Actions CI/CD

### One-time setup

1. Push this repo to GitHub if it isn't already there.
2. Create a service principal scoped to the resource group and store it as a GitHub secret:

   ```bash
   az ad sp create-for-rbac --name spreadtrader-deploy \
     --role contributor \
     --scopes /subscriptions/<subscription-id>/resourceGroups/$RG \
     --sdk-auth
   ```

   Copy the full JSON output into a GitHub repo secret named `AZURE_CREDENTIALS`
   (Settings → Secrets and variables → Actions → New repository secret).

3. Add three more repo secrets: `ACR_NAME`, `AZURE_RESOURCE_GROUP` (=`$RG`), `AZURE_WEBAPP_NAME`
   (=`$APP`) - or hardcode them in the workflow below if you'd rather not manage secrets for
   non-sensitive values.

The workflow at `.github/workflows/deploy-azure.yml` builds both images on every push to `main`,
pushes them to ACR tagged with the commit SHA, and updates the Web App + sidecar to those new
tags - so every deploy is traceable to a specific commit instead of silently floating on `latest`.

### After the first deploy

Trigger settings (`Kite:ApiKey`, `Dashboard:BaseUrl`, the storage mount, etc.) live on the Azure
resource, not in the workflow - the workflow only ever changes which images are running. Change
app settings via `az webapp config appsettings set` (or the Portal) whenever they need to change.

---

## Notes / things worth doing next, not required to go live

- **Trading is live from the moment `Kite:AccessToken` exists.** There's no staging/paper-trading
  mode in this codebase - `OrderDispatchHostedService` places real orders once a trigger fires
  and you're logged in. Test trigger logic with rules that can't realistically fire (thresholds
  far from the current market) before trusting it unattended.
- **Custom domain / TLS**: `*.azurewebsites.net` already gets a free, valid HTTPS certificate -
  nothing to do for a first deploy. Add a custom domain later via `az webapp config hostname add`
  + `az webapp config ssl` if you want one.
- **Scaling to more than one instance** changes the SignalR story - sidecars and the storage mount
  are per-instance, and `api`'s in-memory tick/ranking state (see `InMemoryTickStore` etc.) isn't
  shared across instances. Stay on a single instance (`B1`, no autoscale) unless you're prepared to
  move that state to something shared (Azure SignalR Service + a real database instead of
  in-memory stores/CSV files).
- **Logs**: `az webapp log tail --resource-group $RG --name $APP` streams both containers' stdout
  live - the fastest way to see why a deploy isn't coming up healthy.
