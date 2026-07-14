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

# Publish OUTSIDE $ProjectDir on purpose (see the 2026-07-15 note below) - never point -o at a
# folder inside the project source tree.
$DeployRoot = "D:\LiveSpreadCalculator\.deploy-pradeep-1"
$PublishDir = "$DeployRoot\publish"
$zipPath    = "$DeployRoot\deploy-pradeep-1.zip"

# Everything below runs inside try/catch/finally so that if you launched this by double-clicking
# the file (which opens its own PowerShell window and closes it the instant the script exits -
# success OR failure), you actually get to read what happened before the window disappears. Without
# this, a failed `dotnet publish`/`az` call (bad credentials, a compile error, network hiccup, etc.)
# just flashes red text and vanishes.
try {

Write-Host "==> Building dashboard client..." -ForegroundColor Cyan
Set-Location $ClientDir
npm install
if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
npm run build
if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }

# 2026-07-15: -o used to point at "./publish" INSIDE $ProjectDir. .NET SDK projects implicitly
# include every file under the project directory as a content item (only bin/obj are excluded by
# default) - so the second time this script ever ran, `dotnet publish` saw the previous run's own
# "publish" folder sitting there and copied it INTO the new publish output, producing
# publish\publish. Every run after that nested one level deeper (publish\publish\publish\...),
# completely silently, until it finally exceeded Windows' 260-character path limit and Kudu's zip
# extraction started failing with PathTooLongException. Publishing to $PublishDir - outside
# $ProjectDir entirely - means the SDK's implicit globbing never sees the previous output, so this
# can't recur. Deleting $DeployRoot first on every run is extra insurance (also keeps the zip
# clean of anything from a previous, possibly-different build).
if (Test-Path $DeployRoot) { Remove-Item $DeployRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Write-Host "==> Publishing API (Release)..." -ForegroundColor Cyan
Set-Location $ProjectDir
dotnet publish -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Copying built dashboard into wwwroot..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$PublishDir\wwwroot" | Out-Null
Copy-Item -Path "$ClientDir\dist\*" -Destination "$PublishDir\wwwroot" -Recurse -Force

# App_Data\** (see the .csproj's Content Include comment) copies EVERY App_Data file into every
# publish output. triggers.csv/pnl-alerts.csv/futures_instruments.csv/universe_futures.csv are LIVE
# SERVER STATE written at runtime by the running app, not source-controlled config - az webapp
# deploy's zip overlay OVERWRITES any file path already on the server with whatever is in the zip.
#
# As of 2026-07-15, by explicit request, the Remove-Item stripping below is DISABLED (commented
# out, not deleted, so it can be restored later) - every deploy now DOES overwrite these four
# files on the live server with whatever's in the local repo's App_Data folder, the same way
# users*.csv already behaves (see the note below). Know this before relying on live-only state:
# any trigger/PNL-alert/futures/universe data that only exists on the server (not also committed
# here) will be wiped the next time you run this script - this is exactly the incident that made
# the stripping exist in the first place (see git history around this file). If you need the
# server's live triggers.csv etc. preserved again, uncomment the four Remove-Item lines below.
#
# users.csv/users.pradeep-1.csv/users.pradeep-2.csv are DELIBERATELY NOT stripped either, by
# separate explicit request from before this change - the local repo copy is deployed like any
# other static file and DOES overwrite whatever's live on the server on every deploy. Know this
# before editing credentials only on the server via Kudu: that edit will be wiped on the next
# deploy unless the same change is also made to the local App_Data\users*.csv file first (this is
# exactly the behavior that reset Production's login to the placeholder demo accounts once
# already - see git history around 2026-07-14 for the incident this traded off against).
Write-Host "==> App_Data files are NOT stripped - every deploy overwrites live server state with the local repo copy (see comment above)." -ForegroundColor Cyan
# Remove-Item "$PublishDir\App_Data\triggers.csv" -Force -ErrorAction SilentlyContinue
# Remove-Item "$PublishDir\App_Data\pnl-alerts.csv" -Force -ErrorAction SilentlyContinue
# Remove-Item "$PublishDir\App_Data\futures_instruments.csv" -Force -ErrorAction SilentlyContinue
# Remove-Item "$PublishDir\App_Data\universe_futures.csv" -Force -ErrorAction SilentlyContinue

Set-Location $PublishDir

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
if ($LASTEXITCODE -ne 0) { throw "az webapp deploy failed with exit code $LASTEXITCODE" }

Write-Host "==> Done. $AppUrl" -ForegroundColor Green

}
catch {
    Write-Host ""
    Write-Host "==> DEPLOY FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
}
finally {
    Write-Host ""
    Read-Host "Press Enter to close this window"
}
