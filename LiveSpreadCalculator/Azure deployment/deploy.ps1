# deploy.ps1 - Publish and deploy LiveSpreadCalculator to the Azure Web App
# Run from PowerShell:  .\deploy.ps1
# (If blocked by execution policy: powershell -ExecutionPolicy Bypass -File deploy.ps1)

$ErrorActionPreference = "Stop"

$ProjectDir    = "D:\LiveSpreadCalculator\LiveSpreadCalculator\LiveSpreadCalculator"
$ClientDir     = "D:\LiveSpreadCalculator\LiveSpreadCalculator\SpreadTrader.Dashboard\spreadtrader.dashboard.client"
$ResourceGroup = "live-spread"
$AppName       = "LiveSpreadCalculator"
$AppUrl        = "https://livespreadcalculator-hmgsc4ddf9hba7gv.southindia-01.azurewebsites.net"

# Publish OUTSIDE $ProjectDir on purpose (see the 2026-07-15 note below) - never point -o at a
# folder inside the project source tree.
$DeployRoot = "D:\LiveSpreadCalculator\.deploy-production"
$PublishDir = "$DeployRoot\publish"
$zipPath    = "$DeployRoot\deploy.zip"

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
# users.csv/users.pradeep-1.csv/users.pradeep-2.csv ARE NOW STRIPPED from the publish output (the
# Remove-Item line below), so a deploy NEVER overwrites the live server's user file. This was changed
# 2026-07-15: passwords are now editable on the live server itself (the Profile > Change password
# feature stores a new ENCRYPTED password directly into the server's users.csv). If the deploy kept
# overwriting that file with the local repo copy, every password a user changed on the server would
# be silently wiped on the next deploy and their login reset to whatever's committed here - exactly
# the credential-reset incident this used to cause. Stripping the files means the zip overlay leaves
# the server's own users.csv in place.
#
# Trade-off to know: because the file is no longer in the zip, the server keeps whatever users file
# it already has. A brand-new/empty Web App would therefore start with NO users file until one is
# uploaded once (via Kudu / FTP). To seed or intentionally reset server credentials from the repo,
# comment the users*.csv Remove-Item line out for that one deploy.
#
# The other four App_Data files (triggers/pnl-alerts/futures/universe) are still NOT stripped, by
# the separate explicit request noted above - every deploy overwrites those with the local repo copy.
Write-Host "==> Stripping users*.csv from publish so the deploy does NOT overwrite live server credentials (other App_Data files still overwrite - see comment)." -ForegroundColor Cyan
#Remove-Item "$PublishDir\App_Data\users*.csv" -Force -ErrorAction SilentlyContinue
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

Write-Host "==> Ensuring ASPNETCORE_ENVIRONMENT=Production (so appsettings.Production.json is used)..." -ForegroundColor Cyan
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings ASPNETCORE_ENVIRONMENT=Production | Out-Null

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
