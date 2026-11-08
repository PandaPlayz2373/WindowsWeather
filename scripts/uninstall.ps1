# Remove Windows Weather: stop it, remove the startup entry and the install folder.
# Settings and logs in %APPDATA%\WindowsWeather are removed too unless -KeepSettings is given.
# Usage: powershell -ExecutionPolicy Bypass -File execution/uninstall.ps1 [-KeepSettings]
param([switch]$KeepSettings)
$ErrorActionPreference = "Continue"
$dest = Join-Path $env:LOCALAPPDATA "Programs\WindowsWeather"
$exe = Join-Path $dest "WindowsWeather.exe"
if (Test-Path $exe) { & $exe --quit; Start-Sleep -Milliseconds 800 }
Get-Process WindowsWeather -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "WindowsWeather" -ErrorAction SilentlyContinue
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
if (-not $KeepSettings) {
    $data = Join-Path $env:APPDATA "WindowsWeather"
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
}
Write-Output "Windows Weather removed$(if ($KeepSettings) { ' (settings kept)' })."
