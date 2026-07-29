<#
.SYNOPSIS
    Lists what is actually in a folder on the Plesk host over FTP, so docroot paths can be READ
    rather than guessed.

.DESCRIPTION
    The Plesk deploy scripts need each instance's exact document root. Guessing the folder name for
    an additional domain (e.g. "is it /musing-booth.103-191-208-18.plesk.page/httpdocs, or something
    shorter?") produces a 550 on the first upload and nothing else useful. This just lists a
    directory so you can see the real names.

    Start with no -Path to see the FTP login's own root - on this subscription that is the home
    directory, containing httpdocs (the PRIMARY domain, strange-cray) plus a folder per additional
    domain. Then drill in with -Path to find the httpdocs inside the one you want.

.PARAMETER Path
    Remote directory to list. Defaults to "/" (the FTP login's root).

.PARAMETER Detailed
    Use a full LIST (permissions, size, date) instead of just names - useful for telling a directory
    apart from a file.

.EXAMPLE
    .\List-PleskFtp.ps1
    .\List-PleskFtp.ps1 -Path "/musing-booth.103-191-208-18.plesk.page"
    .\List-PleskFtp.ps1 -Path "/httpdocs" -Detailed
#>

[CmdletBinding()]
param(
    [string]$Path = "/",
    [switch]$Detailed
)

$ErrorActionPreference = 'Stop'

# Same connection details as the deploy scripts.
$FtpHostName = "103.191.208.18"
$FtpPort     = 21
$FtpUser     = if ($env:PLESK_FTP_USER)     { $env:PLESK_FTP_USER }     else { "strange-c_katfn7gx3p" }
$FtpPassword = if ($env:PLESK_FTP_PASSWORD) { $env:PLESK_FTP_PASSWORD } else { 'SpreadCal@123' }

try {

if (-not $Path.StartsWith('/')) { $Path = "/$Path" }

$method = if ($Detailed) {
    [System.Net.WebRequestMethods+Ftp]::ListDirectoryDetails
} else {
    [System.Net.WebRequestMethods+Ftp]::ListDirectory
}

$req = [System.Net.FtpWebRequest]::Create("ftp://${FtpHostName}:${FtpPort}${Path}")
$req.Credentials = New-Object System.Net.NetworkCredential($FtpUser, $FtpPassword)
$req.Method = $method
$req.UsePassive = $true
$req.UseBinary = $false
$req.KeepAlive = $false

$resp   = $req.GetResponse()
$reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
$listing = $reader.ReadToEnd()
$reader.Close()
$resp.Close()

Write-Host ""
Write-Host "ftp://$FtpHostName$Path  (as $FtpUser)" -ForegroundColor Cyan
Write-Host ("-" * 60) -ForegroundColor DarkGray

if ([string]::IsNullOrWhiteSpace($listing)) {
    Write-Host "(empty)" -ForegroundColor DarkYellow
} else {
    $listing.TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host "  $_" }
}

Write-Host ""
Write-Host "Next: pick the folder for the domain you want and list its httpdocs, e.g." -ForegroundColor DarkGray
Write-Host "  .\List-PleskFtp.ps1 -Path `"/<that-folder>/httpdocs`"" -ForegroundColor DarkGray
Write-Host "Whatever path lists successfully is what goes in the deploy script's `$RemoteRoot." -ForegroundColor DarkGray

}
catch {
    Write-Host ""
    Write-Host "ERROR listing ${Path}: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "A 550 here means that path does not exist (or this login cannot see it)." -ForegroundColor Yellow
    Write-Host "Run with no -Path to list the login's root and work down from the real folder names." -ForegroundColor Yellow
}
finally {
    Write-Host ""
    Read-Host "Press Enter to exit"
}
