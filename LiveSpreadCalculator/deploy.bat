@echo off
REM deploy.bat - Publish and deploy LiveSpreadCalculator to the Azure Web App
REM Run from Command Prompt:  deploy.bat

setlocal

set PROJECT_DIR=D:\LiveSpreadCalculator\LiveSpreadCalculator\LiveSpreadCalculator
set CLIENT_DIR=D:\LiveSpreadCalculator\LiveSpreadCalculator\SpreadTrader.Dashboard\spreadtrader.dashboard.client
set RESOURCE_GROUP=live-spread
set APP_NAME=LiveSpreadCalculator
set APP_URL=https://livespreadcalculator-hmgsc4ddf9hba7gv.southindia-01.azurewebsites.net

echo ==^> Building dashboard client...
cd /d "%CLIENT_DIR%"
call npm install
if errorlevel 1 goto :error
call npm run build
if errorlevel 1 goto :error

echo ==^> Publishing API (Release)...
cd /d "%PROJECT_DIR%"
dotnet publish -c Release -o ./publish
if errorlevel 1 goto :error

echo ==^> Copying built dashboard into wwwroot...
if not exist publish\wwwroot mkdir publish\wwwroot
xcopy "%CLIENT_DIR%\dist\*" "publish\wwwroot\" /E /I /Y
if errorlevel 1 goto :error

cd publish

if exist ..\deploy.zip del ..\deploy.zip

echo ==^> Zipping publish output...
tar -a -c -f ../deploy.zip *
if errorlevel 1 goto :error

echo ==^> Checking Azure login...
az account show >nul 2>&1
if errorlevel 1 (
    echo Not logged in - launching az login...
    az login
)

echo ==^> Ensuring ASPNETCORE_ENVIRONMENT=Production (so appsettings.Production.json is used)...
az webapp config appsettings set --resource-group %RESOURCE_GROUP% --name %APP_NAME% --settings ASPNETCORE_ENVIRONMENT=Production >nul
if errorlevel 1 goto :error

echo ==^> Deploying to Azure Web App "%APP_NAME%"...
az webapp deploy --resource-group %RESOURCE_GROUP% --name %APP_NAME% --src-path ../deploy.zip --type zip
if errorlevel 1 goto :error

echo ==^> Done. %APP_URL%
goto :eof

:error
echo Deployment failed - see the error above.
exit /b 1
