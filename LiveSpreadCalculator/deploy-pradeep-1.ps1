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
# publish output, including the shared App_Data\users.csv and pradeep-2's own login file - neither
# belongs in this instance's deployment package (appsettings.pradeep-1.json's UsersCsvPath points
# directly at App_Data/users.pradeep-1.csv, so that file is kept as-is, unrenamed; the others are
# just dead weight/an unnecessary credentials leak if left in the zip).
Write-Host "==> Stripping other instances' login files from the package..." -ForegroundColor Cyan
Remove-Item "./publish/App_Data/users.csv" -Force -ErrorAction SilentlyContinue
Remove-Item "./publish/App_Data/users.pradeep-2.csv" -Force -ErrorAction SilentlyContinue

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
