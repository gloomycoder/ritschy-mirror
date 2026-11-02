<#
  build_installer.ps1 - builds the app self-contained (publish.ps1) and then compiles
  the Inno Setup installer (RitschyMirror-Setup-x.y.z.exe) into installer\out\.

  Prerequisites: .NET 9 SDK + Inno Setup 6 (https://jrsoftware.org/isinfo.php).

  Usage:  powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)  # ..\ (project root)

Write-Host "=== 1/2  Build app (publish.ps1) ===" -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $root "publish.ps1")
if (-not (Test-Path (Join-Path $root "publish\RitschyMirror.exe"))) {
    throw "publish\RitschyMirror.exe missing - build failed."
}

Write-Host "=== 2/2  Compile installer (ISCC) ===" -ForegroundColor Cyan
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found - install Inno Setup 6." }

& $iscc (Join-Path $root "installer\RitschyMirror.iss")
Write-Host ""
Write-Host "Done -> installer\out\RitschyMirror-Setup-*.exe" -ForegroundColor Green
