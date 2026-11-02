<#
  publish.ps1 — builds RitschyMirror self-contained (a single .exe, no .NET needed on the target PC) into .\publish\.
  Afterwards run install.ps1 (copies + shortcuts + optional URL ACL/firewall).

  Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1
#>
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $dir
try {
    Write-Host "=== dotnet publish (self-contained, single-file) ===" -ForegroundColor Cyan
    dotnet publish -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o publish
    $exe = Join-Path $dir "publish\RitschyMirror.exe"
    if (-not (Test-Path $exe)) { throw "Publish failed: $exe not found." }
    Write-Host "Done: $exe" -ForegroundColor Green
    Write-Host "Next step: install.ps1 (on the target PC)" -ForegroundColor Green
}
finally { Pop-Location }
