#!/usr/bin/env bash
# One-time Azure provisioning for SpreadTrader - run this yourself, locally, logged into your own
# Azure account (`az login` first). See DEPLOYMENT.md for what each step does and why.
#
# Usage: bash scripts/azure-setup.sh
# Run from the repo root (same directory as docker-compose.yml).

set -euo pipefail

# ---- 1. Names --------------------------------------------------------------------------------
# Edit LOCATION if you want a different region. The others get a random suffix so they don't
# collide with anyone else's globally-unique Azure names (ACR name, storage account name, and the
# Web App name - which becomes https://$APP.azurewebsites.net - all have to be globally unique).
RG=spreadtrader-rg
LOCATION=centralindia
ACR_NAME=spreadtraderacr$RANDOM
PLAN=spreadtrader-plan
APP=spreadtrader-app$RANDOM
STORAGE=spreadtraderst$RANDOM
SHARE=spreadtrader-data

echo "Resource group:     $RG"
echo "Location:           $LOCATION"
echo "ACR name:           $ACR_NAME"
echo "App Service plan:   $PLAN"
echo "Web App name:       $APP  ->  https://$APP.azurewebsites.net"
echo "Storage account:    $STORAGE"
echo

read -p "Look right? Press Enter to continue, Ctrl+C to abort and edit the script first."

# ---- 2. Resource group + ACR ------------------------------------------------------------------
az group create --name "$RG" --location "$LOCATION"
az acr create --resource-group "$RG" --name "$ACR_NAME" --sku Basic --admin-enabled true

# ---- 3. Build + push both images (in Azure, no local Docker required) ------------------------
az acr build --registry "$ACR_NAME" --image spreadtrader/api:latest \
  --file LiveSpreadCalculator/Dockerfile .

az acr build --registry "$ACR_NAME" --image spreadtrader/web:latest \
  --file SpreadTrader.Dashboard/spreadtrader.dashboard.client/Dockerfile \
  SpreadTrader.Dashboard/spreadtrader.dashboard.client

# ---- 4. Persistent storage for App_Data (trigger rules must survive restarts) -----------------
az storage account create --resource-group "$RG" --name "$STORAGE" --sku Standard_LRS
az storage share create --account-name "$STORAGE" --name "$SHARE"
STORAGE_KEY=$(az storage account keys list --resource-group "$RG" --account-name "$STORAGE" --query "[0].value" -o tsv)

# ---- 5. App Service plan (Linux) ---------------------------------------------------------------
az appservice plan create --resource-group "$RG" --name "$PLAN" --is-linux --sku B1

# ---- 6. Web App (main container = web) + api sidecar -------------------------------------------
ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --query loginServer -o tsv)
ACR_USER=$(az acr credential show --name "$ACR_NAME" --query username -o tsv)
ACR_PASS=$(az acr credential show --name "$ACR_NAME" --query "passwords[0].value" -o tsv)

az webapp create --resource-group "$RG" --plan "$PLAN" --name "$APP" \
  --deployment-container-image-name "$ACR_LOGIN_SERVER/spreadtrader/web:latest"

az webapp config container set --resource-group "$RG" --name "$APP" \
  --container-registry-url "https://$ACR_LOGIN_SERVER" \
  --container-registry-user "$ACR_USER" \
  --container-registry-password "$ACR_PASS"

az webapp config appsettings set --resource-group "$RG" --name "$APP" \
  --settings WEBSITES_PORT=80

az webapp sitecontainers create --resource-group "$RG" --name "$APP" \
  --container-name api \
  --image "$ACR_LOGIN_SERVER/spreadtrader/api:latest" \
  --target-port 8080 \
  --registry-user "$ACR_USER" \
  --registry-password "$ACR_PASS"

az webapp config storage-account add --resource-group "$RG" --name "$APP" \
  --custom-id spreadtrader-data \
  --storage-type AzureFiles \
  --account-name "$STORAGE" \
  --share-name "$SHARE" \
  --access-key "$STORAGE_KEY" \
  --mount-path /data

# ---- 7. App settings ----------------------------------------------------------------------------
APP_URL="https://$APP.azurewebsites.net"

echo
echo "Enter your Kite Connect API credentials (rotate first if the old ones were ever committed -"
echo "see DEPLOYMENT.md section 0):"
read -p "Kite API Key: " KITE_API_KEY
read -sp "Kite API Secret: " KITE_API_SECRET
echo

az webapp config appsettings set --resource-group "$RG" --name "$APP" --settings \
  API_UPSTREAM_HOST=localhost \
  Kite__ApiKey="$KITE_API_KEY" \
  Kite__ApiSecret="$KITE_API_SECRET" \
  Kite__AccessToken="" \
  Dashboard__BaseUrl="$APP_URL" \
  Storage__TriggersCsvPath="/data/triggers.csv" \
  Storage__FuturesCsvPath="/data/futures_instruments.csv" \
  Storage__UniverseCsvPath="/data/universe_futures.csv"

az webapp restart --resource-group "$RG" --name "$APP"

# ---- Summary - save these, you'll need them for GitHub Actions (see DEPLOYMENT.md section 8) --
echo
echo "=================================================================================="
echo "Done. Dashboard: $APP_URL"
echo
echo "Register this redirect URL in the Kite Connect developer console for your API key:"
echo "  $APP_URL/api/auth/callback"
echo
echo "Values you'll need for GitHub Actions secrets:"
echo "  AZURE_RESOURCE_GROUP = $RG"
echo "  AZURE_WEBAPP_NAME    = $APP"
echo "  ACR_NAME             = $ACR_NAME"
echo "=================================================================================="
