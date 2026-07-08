# deploy.ps1 - Publish and deploy LiveSpreadCalculator to the Azure Web App
# Run from PowerShell:  .\deploy.ps1
# (If blocked by execution policy: powershell -ExecutionPolicy Bypass -File deploy.ps1)

$ErrorActionPreference = "Stop"

$ProjectDir    = "D:\LiveSpreadCalculator\LiveSpreadCalculator\LiveSpreadCalculator"
$ClientDir     = "D:\LiveSpreadCalculator\LiveSpreadCalculator\SpreadTrader.Dashboard\spreadtrader.dashboard.client"
$ResourceGroup = "live-spread"
$AppName       = "LiveSpreadCalculator"
$AppUrl        = "https://livespreadcalculator-hmgsc4ddf9hba7gv.southindia-01.azurewebsites.net"

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
# publish output - that now includes the independent instances' own users.pradeep-1.csv /
# users.pradeep-2.csv login files (see deploy-pradeep-1.ps1/deploy-pradeep-2.ps1), which have no
# business in Production's deployment package. Production keeps using the tracked App_Data\
# users.csv as-is (untouched); this just strips the other instances' files out of this zip.
Get-ChildItem "./publish/App_Data/users.pradeep-*.csv" -ErrorAction SilentlyContinue | Remove-Item -Force

Set-Location ./publish

$zipPath = "../deploy.zip"
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

Write-Host "==> Done. $AppUrl" -ForegroundColor Green
