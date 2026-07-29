# deploy-plesk.ps1 - Publish and deploy the PRODUCTION instance to Plesk (Windows/IIS) via FTP
# Run from PowerShell:  .\deploy-plesk.ps1
# (If blocked by execution policy: powershell -ExecutionPolicy Bypass -File deploy-plesk.ps1)
#
# The Plesk counterpart of deploy.ps1 (which deploys Production to Azure), matching
# deploy-pradeep-1-plesk.ps1 / deploy-pradeep-2-plesk.ps1 in every respect except the environment
# name and target docroot. See deploy-pradeep-1-plesk.ps1's header for WHY the Plesk/Windows/IIS
# mechanics differ from the Azure scripts (app_offline.htm restarts instead of az webapp deploy,
# web.config environment injection instead of an Azure App Setting, an FTP docroot instead of a
# Web App).
#
# ASPNETCORE_ENVIRONMENT is "Production" here, which is ASP.NET Core's DEFAULT - so unlike the
# pradeep-* scripts, the web.config injection below is belt-and-braces rather than load-bearing.
# It's still done explicitly so this file never silently depends on that default, and so the
# deployed web.config states the environment plainly instead of leaving it implied.
#
# ============================================================================================
# FILL THESE IN before running:
# ============================================================================================
# Production serves the "musing-booth" domain, which is an ADDITIONAL domain on the SAME Plesk
# subscription as strange-cray (both appear together under one Websites & Domains list - so one
# system user, one home directory, one FTP login shared with the pradeep-* scripts).
#
# Because of that, $RemoteRoot must NOT be /httpdocs: in Plesk only the PRIMARY domain owns
# /httpdocs, and that is strange-cray - i.e. pradeep-1's docroot. Every additional domain gets its
# own folder named after itself. Pointing this at /httpdocs deploys Production straight on top of
# pradeep-1, which is exactly what happened on the first attempt.
#
# Note the additional domain's folder IS the docroot here - there is no httpdocs nested inside it
# (an earlier attempt at .../musing-booth.../httpdocs failed with 550 on the very first write,
# because that subfolder does not exist; an FTP listing of the login root confirms the domain
# folder sits directly alongside httpdocs, cgi-bin, logs and tmp).
#
# To re-confirm at any time: Tools\List-PleskFtp.ps1 lists any remote path, or Websites & Domains >
# click the folder icon next to musing-booth and read File Manager's breadcrumb.
$FtpHostName = "103.191.208.18"
$FtpPort     = 21
$RemoteRoot  = "/musing-booth.103-191-208-18.plesk.page"   # the domain folder itself - see above
$AppUrl      = "https://musing-booth.103-191-208-18.plesk.page"

# ============================================================================================
# Credentials. Hardcoded below by request so this runs with no setup. NOTE this file contains a
# live password - if this repo is ever pushed to a remote (public OR private), rotate it in Plesk
# afterwards. Override without editing the file by setting $env:PLESK_FTP_PASSWORD /
# $env:PLESK_FTP_USER, which take precedence over these defaults.
#
# Same login as the pradeep-* scripts, per instruction. Worth knowing if the first run fails with a
# 530 "login incorrect" or a 550 on the very first upload: Plesk normally gives each SUBSCRIPTION
# its own system user, in which case strange-cray's credentials genuinely cannot reach
# musing-booth's files and you'd need that subscription's own FTP login (its Plesk > Connection
# Info, or Websites & Domains > FTP Access). Nothing is written before login succeeds, so a wrong
# guess here fails safely rather than deploying somewhere unintended.
# ============================================================================================
$DefaultFtpUser     = "strange-c_katfn7gx3p"
$DefaultFtpPassword = 'SpreadCal@123'

$ErrorActionPreference = "Stop"

$EnvironmentName = "Production"

$ProjectDir = "D:\LiveSpreadCalculator\LiveSpreadCalculator\LiveSpreadCalculator"
$ClientDir  = "D:\LiveSpreadCalculator\LiveSpreadCalculator\SpreadTrader.Dashboard\spreadtrader.dashboard.client"
$DeployRoot = "D:\LiveSpreadCalculator\.deploy-production-plesk"
$PublishDir = "$DeployRoot\publish"

try {

$FtpUser = $env:PLESK_FTP_USER
if (-not $FtpUser) { $FtpUser = $DefaultFtpUser }

$FtpPasswordPlain = $env:PLESK_FTP_PASSWORD
if (-not $FtpPasswordPlain) { $FtpPasswordPlain = $DefaultFtpPassword }


Write-Host "==> Building dashboard client..." -ForegroundColor Cyan
Set-Location $ClientDir
npm install
if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
npm run build
if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }

# Publish OUTSIDE $ProjectDir on purpose - same PathTooLongException lesson as deploy-pradeep-2.ps1
# (see that file's 2026-07-15 note): never point -o at a folder inside the project source tree.
if (Test-Path $DeployRoot) { Remove-Item $DeployRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Write-Host "==> Publishing API (Release)..." -ForegroundColor Cyan
Set-Location $ProjectDir
dotnet publish -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Copying built dashboard into wwwroot..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$PublishDir\wwwroot" | Out-Null
Copy-Item -Path "$ClientDir\dist\*" -Destination "$PublishDir\wwwroot" -Recurse -Force

# ---- Bake ASPNETCORE_ENVIRONMENT into web.config ----------------------------------------------
function Set-WebConfigEnvironment {
    param([string]$WebConfigPath, [string]$Name, [string]$Value)

    if (-not (Test-Path $WebConfigPath)) {
        Write-Host "   WARNING: no web.config in the publish output - skipping environment injection." -ForegroundColor Yellow
        return
    }

    [xml]$xml = Get-Content $WebConfigPath
    $aspNetCore = $xml.SelectSingleNode("//aspNetCore")
    if (-not $aspNetCore) {
        Write-Host "   WARNING: web.config has no <aspNetCore> element - skipping environment injection." -ForegroundColor Yellow
        return
    }

    $envVars = $aspNetCore.SelectSingleNode("environmentVariables")
    if (-not $envVars) {
        $envVars = $xml.CreateElement("environmentVariables")
        [void]$aspNetCore.AppendChild($envVars)
    }

    $existing = $envVars.SelectSingleNode("environmentVariable[@name='$Name']")
    if ($existing) {
        $existing.SetAttribute("value", $Value)
    } else {
        $node = $xml.CreateElement("environmentVariable")
        $node.SetAttribute("name", $Name)
        $node.SetAttribute("value", $Value)
        [void]$envVars.AppendChild($node)
    }

    $xml.Save($WebConfigPath)
    Write-Host "   web.config: $Name=$Value" -ForegroundColor DarkGray
}

Write-Host "==> Injecting ASPNETCORE_ENVIRONMENT=$EnvironmentName into web.config..." -ForegroundColor Cyan
Set-WebConfigEnvironment -WebConfigPath "$PublishDir\web.config" -Name "ASPNETCORE_ENVIRONMENT" -Value $EnvironmentName

# PRODUCTION CREDENTIALS. Leave this commented out for the FIRST deploy only, so App_Data/users.csv
# uploads and seeds the login file on this new host. AFTER that first successful deploy, UNCOMMENT
# it - otherwise every redeploy overwrites the live server's users.csv and resets any password a
# user has changed via Profile > Change password. That exact credential-reset incident is why the
# Azure scripts carry the same warning.
# Remove-Item "$PublishDir\App_Data\users*.csv" -Force -ErrorAction SilentlyContinue

# ---- FTP plumbing ------------------------------------------------------------------------------
function Invoke-FtpRequest {
    param([string]$RemotePath, [string]$Method, [byte[]]$Body)

    $uri = "ftp://${FtpHostName}:${FtpPort}${RemotePath}"
    $req = [System.Net.FtpWebRequest]::Create($uri)
    $req.Credentials = New-Object System.Net.NetworkCredential($FtpUser, $FtpPasswordPlain)
    $req.Method = $Method
    $req.UsePassive = $true
    $req.UseBinary = $true
    $req.KeepAlive = $false

    if ($Body) {
        $req.ContentLength = $Body.Length
        $stream = $req.GetRequestStream()
        $stream.Write($Body, 0, $Body.Length)
        $stream.Close()
    }

    $resp = $req.GetResponse()
    $resp.Close()
}

function New-FtpDirectory {
    param([string]$RemotePath)
    try {
        Invoke-FtpRequest -RemotePath $RemotePath -Method ([System.Net.WebRequestMethods+Ftp]::MakeDirectory)
    } catch {
        # Almost always "550 already exists" after the first run - swallowed deliberately, since a
        # real problem (bad credentials/host/permission) surfaces loudly on the next file upload.
    }
}

function Send-FtpFile {
    param([string]$LocalFile, [string]$RemotePath)
    $bytes = [System.IO.File]::ReadAllBytes($LocalFile)
    Invoke-FtpRequest -RemotePath $RemotePath -Method ([System.Net.WebRequestMethods+Ftp]::UploadFile) -Body $bytes
}

function Remove-FtpFile {
    param([string]$RemotePath)
    try {
        Invoke-FtpRequest -RemotePath $RemotePath -Method ([System.Net.WebRequestMethods+Ftp]::DeleteFile)
    } catch {
        Write-Host "   (could not delete $RemotePath - $($_.Exception.Message))" -ForegroundColor DarkYellow
    }
}

# ---- Take the app offline so IIS releases its file locks ---------------------------------------
Write-Host "==> Taking app offline (app_offline.htm)..." -ForegroundColor Cyan
$offlineLocal = "$DeployRoot\app_offline.htm"
@"
<!DOCTYPE html>
<html><head><title>Deploying</title></head>
<body style="font-family:sans-serif;padding:40px">
<h2>Deployment in progress</h2>
<p>This instance is being updated and will be back shortly.</p>
</body></html>
"@ | Out-File -FilePath $offlineLocal -Encoding utf8
Send-FtpFile -LocalFile $offlineLocal -RemotePath "$RemoteRoot/app_offline.htm"
Start-Sleep -Seconds 3

# ---- Upload -------------------------------------------------------------------------------------
Write-Host "==> Uploading publish output to ${FtpHostName}${RemoteRoot} (hundreds of files - can take several minutes)..." -ForegroundColor Cyan

New-FtpDirectory -RemotePath $RemoteRoot
$dirs = Get-ChildItem -Path $PublishDir -Recurse -Directory | Sort-Object FullName
foreach ($d in $dirs) {
    $rel = $d.FullName.Substring($PublishDir.Length) -replace '\\', '/'
    New-FtpDirectory -RemotePath "$RemoteRoot$rel"
}

$files = Get-ChildItem -Path $PublishDir -Recurse -File
$count = 0
$failed = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($PublishDir.Length) -replace '\\', '/'
    try {
        Send-FtpFile -LocalFile $f.FullName -RemotePath "$RemoteRoot$rel"
    } catch {
        $failed += "$rel  ->  $($_.Exception.Message)"
    }
    $count++
    if ($count % 25 -eq 0) { Write-Host "   ...$count/$($files.Count)" -ForegroundColor DarkGray }
}
Write-Host "   $count file(s) processed, $($failed.Count) failed." -ForegroundColor DarkGray

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "   FAILED UPLOADS - the app is still OFFLINE on purpose (app_offline.htm left in place)" -ForegroundColor Red
    Write-Host "   so a half-deployed app doesn't start serving. Fix these and re-run:" -ForegroundColor Red
    $failed | Select-Object -First 15 | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    throw "$($failed.Count) file(s) failed to upload - see above."
}

# ---- Bring it back online ----------------------------------------------------------------------
Write-Host "==> Bringing app back online (removing app_offline.htm)..." -ForegroundColor Cyan
Remove-FtpFile -RemotePath "$RemoteRoot/app_offline.htm"

Write-Host ""
Write-Host "==> Done. $AppUrl" -ForegroundColor Green
Write-Host ""
Write-Host "    First-deploy checklist (only needed once):" -ForegroundColor Yellow
Write-Host "      - Confirm the server has the ASP.NET Core 8 Hosting Bundle installed. Plesk Windows" -ForegroundColor Yellow
Write-Host "        shared hosting often ships only .NET Framework; without the bundle IIS cannot run" -ForegroundColor Yellow
Write-Host "        this app at all and you'll get HTTP 500.19 / 502.5. Ask the host if unsure." -ForegroundColor Yellow
Write-Host "      - Register $AppUrl/api/auth/callback as the redirect URL in the Kite and/or Dhan" -ForegroundColor Yellow
Write-Host "        developer console for this instance's API key." -ForegroundColor Yellow
Write-Host "      - Set Dashboard__BaseUrl for this host (appsettings.Production.json currently points" -ForegroundColor Yellow
Write-Host "        at the old Azure URL) or CORS + the OAuth redirect will still target Azure." -ForegroundColor Yellow
Write-Host "      - After this first deploy succeeds, UNCOMMENT the users*.csv Remove-Item line in" -ForegroundColor Yellow
Write-Host "        this script so later deploys stop overwriting live passwords." -ForegroundColor Yellow

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
