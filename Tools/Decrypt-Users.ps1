<#
.SYNOPSIS
    Decrypts and displays the passwords stored in App_Data/users.csv.

.DESCRIPTION
    The LiveSpreadCalculator backend stores each user's password ENCRYPTED (reversible AES-256-CBC -
    see LiveSpreadCalculator/Auth/PasswordProtector.cs), not as a one-way hash, precisely so the
    original password can be recovered by anyone holding the key. This script reproduces that exact
    scheme and prints email + decrypted password for every row.

    Wire format of an encrypted field:  "enc:" + Base64( IV(16 bytes) || ciphertext )
      Key    = SHA-256(UTF8(secret))     -> 32 bytes (AES-256)
      IV     = first 16 bytes of the blob
      Cipher = AES / CBC / PKCS7
    Legacy plaintext rows (no "enc:" prefix) are shown as-is.

.PARAMETER CsvPath
    Path to users.csv. Defaults to ..\LiveSpreadCalculator\App_Data\users.csv relative to this script.

.PARAMETER Key
    The encryption secret. If omitted, the script reads Security:PasswordKey (falling back to Jwt:Key)
    from appsettings.json next to the CSV's project. Must match the backend's key exactly, otherwise
    decryption fails. Prefer passing the value that's actually in effect (e.g. the env var
    Security__PasswordKey) rather than trusting the committed appsettings default.

.EXAMPLE
    .\Decrypt-Users.ps1
    .\Decrypt-Users.ps1 -Key "my-production-secret"
    .\Decrypt-Users.ps1 -CsvPath "D:\...\App_Data\users.csv" -Key $env:Security__PasswordKey
#>

[CmdletBinding()]
param(
    [string]$CsvPath,
    [string]$Key
)

$ErrorActionPreference = 'Stop'

# Wrap everything so the window pauses before closing - whether it finished cleanly or threw - when
# the script is launched by double-clicking (which otherwise closes the console the instant it ends).
try {

# ---- Resolve paths ----------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir '..\LiveSpreadCalculator'

if (-not $CsvPath) {
    $CsvPath = Join-Path $projectDir 'App_Data\users.csv'
}
if (-not (Test-Path $CsvPath)) {
    throw "users.csv not found at: $CsvPath"
}

# ---- Resolve key (param > interactive prompt, defaulting to appsettings Security:PasswordKey / -----
# ---- Jwt:Key as a suggestion) -------------------------------------------------------------------
if (-not $Key) {
    # Each instance uses its own key, so pick the appsettings file that matches the CSV's name:
    #   users.pradeep-1.csv -> appsettings.pradeep-1.json, users.pradeep-2.csv -> ...pradeep-2.json,
    #   anything else (users.csv) -> base appsettings.json.
    $csvName = Split-Path $CsvPath -Leaf
    if     ($csvName -like '*pradeep-1*') { $appSettingsPath = Join-Path $projectDir 'appsettings.pradeep-1.json' }
    elseif ($csvName -like '*pradeep-2*') { $appSettingsPath = Join-Path $projectDir 'appsettings.pradeep-2.json' }
    else                                  { $appSettingsPath = Join-Path $projectDir 'appsettings.json' }

    $suggestedKey = $null
    if (Test-Path $appSettingsPath) {
        # appsettings.json in this project contains // comments, which ConvertFrom-Json rejects. Strip
        # single-line // comments (naively but sufficiently for this file) before parsing.
        $raw = Get-Content -Path $appSettingsPath -Raw
        $noComments = ($raw -split "`n" | ForEach-Object { $_ -replace '^\s*//.*$', '' }) -join "`n"
        $config = $noComments | ConvertFrom-Json

        if ($config.Security -and $config.Security.PasswordKey) {
            $suggestedKey = $config.Security.PasswordKey
        }
        elseif ($config.Jwt -and $config.Jwt.Key) {
            $suggestedKey = $config.Jwt.Key
        }
    }

    # Ask interactively rather than silently trusting the committed appsettings value - if the real
    # Azure Web App overrides Security__PasswordKey via an environment variable (which it should),
    # the committed file's key is just a placeholder and will NOT decrypt real rows. Press Enter to
    # accept the suggested/committed key (fine for local/dev-only data), or paste the real key (e.g.
    # copy-pasted from the Web App's Application Settings) to decrypt production data correctly.
    if ($suggestedKey) {
        Write-Host "Committed key found in $(Split-Path $appSettingsPath -Leaf): $suggestedKey" -ForegroundColor DarkYellow
        $typed = Read-Host "Press Enter to use this key, or paste the real key (e.g. Security__PasswordKey from Azure) if it's different"
        $Key = if ([string]::IsNullOrWhiteSpace($typed)) { $suggestedKey } else { $typed }
    }
    else {
        $Key = Read-Host "No key found in appsettings - paste the encryption key (Security:PasswordKey / Security__PasswordKey)"
        if ([string]::IsNullOrWhiteSpace($Key)) {
            throw "No key provided."
        }
    }
}

# ---- Crypto helpers (must mirror PasswordProtector.cs) ----------------------------------------
function Get-KeyBytes([string]$secret) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($secret)) }
    finally { $sha.Dispose() }
}

function Decrypt-Value([string]$value, [byte[]]$keyBytes) {
    if (-not $value.StartsWith('enc:')) {
        return $value  # legacy plaintext row
    }

    $combined = [Convert]::FromBase64String($value.Substring(4))
    if ($combined.Length -le 16) { throw "Encrypted value too short." }

    $iv = New-Object byte[] 16
    [Array]::Copy($combined, 0, $iv, 0, 16)
    $cipher = New-Object byte[] ($combined.Length - 16)
    [Array]::Copy($combined, 16, $cipher, 0, $cipher.Length)

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $keyBytes
        $aes.IV = $iv
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $decryptor = $aes.CreateDecryptor()
        try {
            $plain = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
            return [System.Text.Encoding]::UTF8.GetString($plain)
        }
        finally { $decryptor.Dispose() }
    }
    finally { $aes.Dispose() }
}

# ---- Read, decrypt, print ---------------------------------------------------------------------
$keyBytes = Get-KeyBytes $Key
$lines = Get-Content -Path $CsvPath
$results = @()

# Skip header row (line 0); ignore blanks and # comments.
for ($i = 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i].Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }

    $comma = $line.IndexOf(',')
    if ($comma -le 0) { continue }

    $email = $line.Substring(0, $comma).Trim()
    $stored = $line.Substring($comma + 1)

    try {
        $plain = Decrypt-Value $stored $keyBytes
        $status = if ($stored.StartsWith('enc:')) { 'encrypted' } else { 'plaintext' }
    }
    catch {
        $plain = "<decrypt failed: $($_.Exception.Message)>"
        $status = 'error'
    }

    $results += [PSCustomObject]@{
        Email    = $email
        Password = $plain
        Stored   = $status
    }
}

$results | Format-Table -AutoSize

}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Write-Host ""
    Read-Host "Press Enter to exit"
}
