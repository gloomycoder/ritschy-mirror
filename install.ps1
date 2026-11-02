<#
  install.ps1 - install RitschyMirror locally (on ANY Windows PC).
  Copies the publish output to %LOCALAPPDATA%\RitschyMirror, creates a Start menu +
  (optional) autostart shortcut and - when run as admin - reserves a
  URL ACL + firewall rule for the HTTP agent (so the cockpit can reach it over the network).

  Usage:
    powershell -ExecutionPolicy Bypass -File install.ps1                 # default (port 8788)
    powershell -ExecutionPolicy Bypass -File install.ps1 -AgentPort 8790 # different port
    powershell -ExecutionPolicy Bypass -File install.ps1 -NoAutostart    # without login autostart
  Run as administrator for URL ACL/firewall (network reachability).

  ASCII-only (Windows PowerShell 5.1 reads .ps1 files without BOM in the ANSI code page).
#>
[CmdletBinding()]
param(
    [int]$AgentPort = 8788,
    [switch]$NoAutostart,
    [switch]$NoNetwork   # skip URL ACL + firewall rule (local/localhost only)
)
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir "publish"
$exeSrc = Join-Path $src "RitschyMirror.exe"
if (-not (Test-Path $exeSrc)) { throw "No publish/RitschyMirror.exe - run publish.ps1 first." }

$dest = Join-Path $env:LOCALAPPDATA "RitschyMirror"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Copy files - do NOT overwrite an existing config on updates.
$keep = @("mirror_config.json", "app_settings.json")
Get-ChildItem $src -File | ForEach-Object {
    $target = Join-Path $dest $_.Name
    if (($keep -contains $_.Name) -and (Test-Path $target)) { return }
    Copy-Item $_.FullName $target -Force
}
$exe = Join-Path $dest "RitschyMirror.exe"
Write-Host "Installed to: $dest" -ForegroundColor Green

# Start menu shortcut
$wsh = New-Object -ComObject WScript.Shell
function New-Lnk($path) { $l = $wsh.CreateShortcut($path); $l.TargetPath = $exe; $l.WorkingDirectory = $dest; $l.Save() }
New-Lnk (Join-Path ([Environment]::GetFolderPath("Programs")) "RitschyMirror.lnk")

# Login autostart
if (-not $NoAutostart) {
    New-Lnk (Join-Path ([Environment]::GetFolderPath("Startup")) "RitschyMirror.lnk")
    Write-Host "Login autostart configured." -ForegroundColor Green
}

# Network reachability of the HTTP agent (needs admin) - otherwise fall back to localhost.
if (-not $NoNetwork) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
    if ($isAdmin) {
        try {
            $url = "http://+:$AgentPort/"
            cmd /c "netsh http add urlacl url=$url user=$env:USERNAME" | Out-Null
            cmd /c "netsh advfirewall firewall add rule name=`"RitschyMirror Agent`" dir=in action=allow protocol=TCP localport=$AgentPort" | Out-Null
            Write-Host "URL ACL + firewall set for $url." -ForegroundColor Green
        } catch { Write-Host "URL ACL/firewall failed: $_" -ForegroundColor Yellow }
    } else {
        Write-Host "Note: not running as admin - agent is only reachable via localhost." -ForegroundColor Yellow
        Write-Host "  For cockpit remote control, run install.ps1 ONCE as admin (URL ACL+firewall)." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Start: `"$exe`"  -> tray icon -> Settings." -ForegroundColor Cyan
Write-Host "Cockpit: connector 'gaming_pc' -> field 'agent_port' = $AgentPort (host = IP of this PC)." -ForegroundColor Cyan
