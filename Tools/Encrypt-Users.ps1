<#
.SYNOPSIS
    Encrypts the plaintext passwords in a users CSV file IN PLACE.

.DESCRIPTION
    The backend also does this automatically on startup (CsvUserStore.MigrateToEncrypted), but that
    only touches the file the running instance points at. This script lets you encrypt a users CSV
    directly - useful for encrypting the local repo copies, or any file, without running the app.

    It reproduces PasswordProtector.cs exactly:
        Key    = SHA-256(UTF8(secret))     -> 32 bytes (AES-256)
        IV     = 16 random bytes, fresh per row, stored as the first 16 bytes of the blob
        Cipher = AES / CBC / PKCS7
        Field  = "enc:" + Base64( IV || ciphertext )
    Rows that are ALREADY encrypted (start with "enc:") are left untouched, so it's safe to run more
    than once. A timestamped .bak copy of the file is written before anything changes.

    KEY SELECTION matters - each instance uses its own key:
      users.csv            -> appsettings.json           (Security:PasswordKey, else Jwt:Key)
      users.pradeep-1.csv  -> appsettings.pradeep-1.json
      users.pradeep-2.csv  -> appsettings.pradeep-2.json
    The right appsettings file is picked automatically from the CSV's name; override with -Key or
    -AppSettingsPath. If the live server overrides Security__PasswordKey via an env var, pass that
    same value with -Key here or the app won't be able to decrypt what this writes.

.PARAMETER CsvPath
    Path to the users CSV. Defaults to ..\LiveSpreadCalculator\App_Data\users.csv.

.PARAMETER Key
    Encryption secret. If omitted, read from the (auto-selected) appsettings file.

.PARAMETER AppSettingsPath
    Override which appsettings file the key is read from. Auto-selected from CsvPath if omitted.

.EXAMPLE
    .\Encrypt-Users.ps1
    .\Encrypt-Users.ps1 -CsvPath "..\LiveSpreadCalculator\App_Data\users.pradeep-1.csv"
    .\Encrypt-Users.ps1 -CsvPath "...\users.pradeep-2.csv" -Key $env:Security__PasswordKey
#>

[CmdletBinding()]
param(
    [string]$CsvPath,
    [string]$Key,
    [string]$AppSettingsPath
)

$ErrorActionPreference = 'Stop'

# Wrap everything so the window pauses before closing when launched by double-click.
try {

# ---- Resolve paths ----------------------------------------------------------------------------
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir '..\LiveSpreadCalculator'

if (-not $CsvPath) {
    $CsvPath = Join-Path $projectDir 'App_Data\users.csv'
}
if (-not (Test-Path $CsvPath)) {
    throw "users CSV not found at: $CsvPath"
}

# Auto-select the appsettings file (and therefore the key) from the CSV's name unless overridden.
if (-not $AppSettingsPath) {
    $csvName = Split-Path $CsvPath -Leaf
    if     ($csvName -like '*pradeep-1*') { $AppSettingsPath = Join-Path $projectDir 'appsettings.pradeep-1.json' }
    elseif ($csvName -like '*pradeep-2*') { $AppSettingsPath = Join-Path $projectDir 'appsettings.pradeep-2.json' }
    else                                  { $AppSettingsPath = Join-Path $projectDir 'appsettings.json' }
}

# ---- Resolve key (param > appsettings Security:PasswordKey > appsettings Jwt:Key) --------------
if (-not $Key) {
    if (-not (Test-Path $AppSettingsPath)) {
        throw "No -Key given and appsettings not found at: $AppSettingsPath"
    }

    # These appsettings files contain // comments, which ConvertFrom-Json rejects - strip them first.
    $raw = Get-Content -Path $AppSettingsPath -Raw
    $noComments = ($raw -split "`n" | ForEach-Object { $_ -replace '^\s*//.*$', '' }) -join "`n"
    $config = $noComments | ConvertFrom-Json

    if ($config.Security -and $config.Security.PasswordKey) {
        $Key = $config.Security.PasswordKey
    }
    elseif ($config.Jwt -and $config.Jwt.Key) {
        $Key = $config.Jwt.Key
    }

    if (-not $Key) {
        throw "Could not find Security:PasswordKey or Jwt:Key in $AppSettingsPath. Pass -Key explicitly."
    }
    Write-Host "Key: read from $(Split-Path $AppSettingsPath -Leaf) (override with -Key if the server uses a different Security__PasswordKey)." -ForegroundColor DarkYellow
}

# ---- Crypto helpers (must mirror PasswordProtector.cs) ----------------------------------------
function Get-KeyBytes([string]$secret) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($secret)) }
    finally { $sha.Dispose() }
}

function Encrypt-Value([string]$plaintext, [byte[]]$keyBytes) {
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $keyBytes
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.GenerateIV()
        $iv = $aes.IV

        $encryptor = $aes.CreateEncryptor()
        try {
            $ptBytes = [System.Text.Encoding]::UTF8.GetBytes($plaintext)
            $cipher = $encryptor.TransformFinalBlock($ptBytes, 0, $ptBytes.Length)
        }
        finally { $encryptor.Dispose() }

        $combined = New-Object byte[] ($iv.Length + $cipher.Length)
        [Array]::Copy($iv, 0, $combined, 0, $iv.Length)
        [Array]::Copy($cipher, 0, $combined, $iv.Length, $cipher.Length)
        return 'enc:' + [Convert]::ToBase64String($combined)
    }
    finally { $aes.Dispose() }
}

# ---- Back up, encrypt plaintext rows, write back ----------------------------------------------
$backup = "$CsvPath.bak-$(Get-Date -Format 'yyyyMMddHHmmss')"
Copy-Item -Path $CsvPath -Destination $backup -Force
Write-Host "Backup written: $backup" -ForegroundColor DarkGray

$keyBytes = Get-KeyBytes $Key
$lines = Get-Content -Path $CsvPath
$out = New-Object System.Collections.Generic.List[string]
$encrypted = 0
$skipped = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    $original = $lines[$i]
    $line = $original.Trim()

    # Keep the header row (line 0), blanks and # comments exactly as-is.
    if ($i -eq 0 -or $line.Length -eq 0 -or $line.StartsWith('#')) {
        $out.Add($original)
        continue
    }

    $comma = $line.IndexOf(',')
    if ($comma -le 0) {
        $out.Add($original)
        continue
    }

    $email = $line.Substring(0, $comma).Trim()
    $stored = $line.Substring($comma + 1)

    if ($stored.StartsWith('enc:')) {
        $out.Add("$email,$stored")   # already encrypted - leave it
        $skipped++
    }
    else {
        $out.Add("$email,$(Encrypt-Value $stored $keyBytes)")
        $encrypted++
    }
}

Set-Content -Path $CsvPath -Value $out -Encoding UTF8

Write-Host ""
Write-Host "Done: $CsvPath" -ForegroundColor Green
Write-Host "  encrypted $encrypted row(s), left $skipped already-encrypted row(s) untouched." -ForegroundColor Green

}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Write-Host ""
    Read-Host "Press Enter to exit"
}
