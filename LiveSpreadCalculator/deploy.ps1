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
