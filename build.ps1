# Builds both executables with the in-box C# compiler — no SDK, no NuGet, nothing to install.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "In-box C# compiler not found at $csc (Windows x64 with .NET Framework 4.x required)." }

New-Item -ItemType Directory -Force -Path 'bin' | Out-Null
if (-not (Test-Path 'assets\horizon-offline.ico')) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'tools\make-icon.ps1'
}

$refs = @(
    "-r:$fw\System.dll",
    "-r:$fw\System.Core.dll",
    "-r:$fw\System.Windows.Forms.dll",
    "-r:$fw\System.Drawing.dll"
)
$icon = '/win32icon:assets\horizon-offline.ico'

# 1) the player — HorizonOffline.exe
$args1 = @('/nologo', '/codepage:65001', '/target:winexe', $icon, '/out:bin\HorizonOffline.exe') + $refs + @('src\Common.cs', 'src\HorizonOffline.cs')
Write-Host 'Building bin\HorizonOffline.exe ...'
& $csc @args1
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }

# 2) the installer — HorizonOfflineSetup.exe, with the player embedded as the "payload" resource
$refs2 = $refs + @(
    "-r:$fw\System.Web.Extensions.dll",
    "-r:$fw\System.IO.Compression.dll",
    "-r:$fw\System.IO.Compression.FileSystem.dll"
)
$args2 = @('/nologo', '/codepage:65001', '/target:winexe', $icon, '/out:bin\HorizonOfflineSetup.exe', '/resource:bin\HorizonOffline.exe,payload') + $refs2 + @('src\Common.cs', 'src\Setup.cs')
Write-Host 'Building bin\HorizonOfflineSetup.exe ...'
& $csc @args2
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }

Write-Host ''
Write-Host 'Done:'
Write-Host '  bin\HorizonOffline.exe        the player (put it next to horizon-offline.ini)'
Write-Host '  bin\HorizonOfflineSetup.exe   the installer (single file, payload embedded)'
