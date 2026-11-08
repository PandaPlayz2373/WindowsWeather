# Build Windows Weather (Native AOT) and install it for the current user, then start it.
#   - exe:      %LOCALAPPDATA%\Programs\WindowsWeather\WindowsWeather.exe
#   - startup:  HKCU\Software\Microsoft\Windows\CurrentVersion\Run  "WindowsWeather"
#   - settings: %APPDATA%\WindowsWeather\ (settings.json, state.json, log.txt), created by the app
# Usage: powershell -ExecutionPolicy Bypass -File execution/install.ps1 [-NoBuild] [-NoStart]
param([switch]$NoBuild, [switch]$NoStart)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root ".tmp\publish"
$dest = Join-Path $env:LOCALAPPDATA "Programs\WindowsWeather"
$exe = Join-Path $dest "WindowsWeather.exe"

if (-not $NoBuild) {
    # The AOT linker step runs vcvarsall, which needs vswhere.exe on PATH (it is not there by default).
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
    Write-Output "Building (Native AOT)..."
    & dotnet publish (Join-Path $root "app\WindowsWeather.csproj") -c Release -r win-x64 -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
$built = Join-Path $publish "WindowsWeather.exe"
if (-not (Test-Path $built)) { throw "No build at $built" }

# Stop a running copy (politely first, so it can save state and remove its tray icon).
$running = Get-Process WindowsWeather -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "Stopping the running copy..."
    & $built --quit
    Start-Sleep -Milliseconds 800
    Get-Process WindowsWeather -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

New-Item -ItemType Directory -Force $dest | Out-Null
# The old exe can stay locked for a few seconds after it exits (antivirus scanning it): retry.
for ($try = 1; ; $try++) {
    try { Copy-Item $built $exe -Force -ErrorAction Stop; break }
    catch { if ($try -ge 20) { throw }; Start-Sleep -Seconds 1 }
}
Write-Output "Installed $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"

$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $run -Name "WindowsWeather" -Value "`"$exe`""
Write-Output "Starts with Windows (Run key set)"

if (-not $NoStart) {
    # Start detached from this console.
    Start-Process -FilePath "explorer.exe" -ArgumentList "`"$exe`""
    for ($i = 0; $i -lt 40 -and -not (Get-Process WindowsWeather -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    $p = Get-Process WindowsWeather -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { Write-Output "Running (pid $($p.Id))" } else { Write-Output "Started, but the process is not visible yet (antivirus may be scanning it the first time)" }
}
