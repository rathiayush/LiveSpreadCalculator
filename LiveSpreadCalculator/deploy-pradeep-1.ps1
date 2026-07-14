# deploy-pradeep-1.ps1 - Publish and deploy the independent "pradeep-1" instance to its own Azure Web App
# Run from PowerShell:  .\deploy-pradeep-1.ps1
# (If blocked by execution policy: powershell -ExecutionPolicy Bypass -File deploy-pradeep-1.ps1)
#
# Mirrors deploy.ps1 (the Production deploy script) exactly, except:
#   - $AppName targets this instance's own Azure Web App, not Production's
#   - ASPNETCORE_ENVIRONMENT is set to "pradeep-1" (not "Production"), so this Web App loads
#     appsettings.pradeep-1.json instead of appsettings.Production.json
# Same publish output (dotnet publish + the built dashboard client) is deployed either way - the
# two instances run identical code, just pointed at different config/App_Data via the environment
# name.

$ErrorActionPreference = "Stop"

$ProjectDir    = "D:\LiveSpreadCalculator\LiveSpreadCalculator\LiveSpreadCalculator"
$ClientDir     = "D:\LiveSpreadCalculator\LiveSpreadCalculator\SpreadTrader.Dashboard\spreadtrader.dashboard.client"
$ResourceGroup = "live-pradeep1"
$AppName       = "P1-LiveSpreadCalculator"
$AppUrl        = "https://p1-livespreadcalculator-bggucycqavegaset.southindia-01.azurewebsites.net"
$EnvironmentName = "pradeep-1"

Write-Host "==> Building dashboard client..." -ForegroundColor Cyan
Set-Location $ClientDir
npm install
npm run build

Write-Host "==> Publishing API (Release)..." -ForegroundColor Cyan
Set-Location $ProjectDir
dotnet publish -c Release -o ./publish

Write-Host "==> Copying built dashboard into wwwroot..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "./publish/wwwroot" | Out-Null
Copy-Item -Path "$ClientDir\dist\*" -Destination "./publish/wwwroot" -Recurse -Force

# App_Data\** (see the .csproj's Content Include comment) copies EVERY App_Data file into every
# publish output. triggers.csv/pnl-alerts.csv/futures_instruments.csv/universe_futures.csv are LIVE
# SERVER STATE written at runtime by the running app, not source-controlled config - az webapp
# deploy's zip overlay OVERWRITES any file path already on the server with whatever is in the zip,
# so leaving these in would silently wipe out live state on every deploy. Stripping them here means
# a deploy only ever touches code for these four - the server keeps whatever it already has.
#
# users.csv/users.pradeep-1.csv/users.pradeep-2.csv are DELIBERATELY NOT stripped, by explicit
# request - the local repo copy is deployed like any other static file, the same as it always was,
# and DOES overwrite whatever's live on the server on every deploy. Know this before editing
# credentials only on the server via Kudu: that edit will be wiped on the next deploy unless the
# same change is also made to the local App_Data\users*.csv file first (this is exactly the
# behavior that reset Production's login to the placeholder demo accounts once already - see
# git history around 2026-07-14 for the incident this traded off against).
Write-Host "==> Stripping live server-state files from the package (never overwrite what's already on the server)..." -ForegroundColor Cyan
Remove-Item "./publish/App_Data/triggers.csv" -Force -ErrorAction SilentlyContinue
Remove-Item "./publish/App_Data/pnl-alerts.csv" -Force -ErrorAction SilentlyContinue
Remove-Item "./publish/App_Data/futures_instruments.csv" -Force -ErrorAction SilentlyContinue
Remove-Item "./publish/App_Data/universe_futures.csv" -Force -ErrorAction SilentlyContinue

Set-Location ./publish

$zipPath = "../deploy-pradeep-1.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

Write-Host "==> Zipping publish output..." -ForegroundColor Cyan
Compress-Archive -Path * -DestinationPath $zipPath -Force

Write-Host "==> Checking Azure login..." -ForegroundColor Cyan
$account = az account show 2>$null
if (-not $account) {
    Write-Host "Not logged in - launching az login..." -ForegroundColor Yellow
    az login
}

Write-Host "==> Ensuring ASPNETCORE_ENVIRONMENT=$EnvironmentName (so appsettings.$EnvironmentName.json is used)..." -ForegroundColor Cyan
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings ASPNETCORE_ENVIRONMENT=$EnvironmentName | Out-Null

Write-Host "==> Deploying to Azure Web App '$AppName'..." -ForegroundColor Cyan
az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zipPath --type zip

Write-Host "==> Done. $AppUrl" -ForegroundColor Green
