# Installs (or updates) MagicKeyBattery into a fixed per-user location and registers it to start at sign-in.
#
#   %LOCALAPPDATA%\Programs\MagicKeyBattery\MagicKeyBattery.exe   (single file, needs the .NET 9 Desktop Runtime)
#   %LOCALAPPDATA%\Programs\MagicKeyBattery\MagicKeyBattery-data\  (settings, key layout, battery history)
#
# Usage:  .\install.ps1            build from this folder and install
#         .\install.ps1 -NoStart   install but do not launch afterwards
#         .\install.ps1 -Uninstall remove the startup task, the Start Menu shortcut and the program (keeps the data folder)
param([switch]$NoStart, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MagicKeyBattery'
$exe = Join-Path $dest 'MagicKeyBattery.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MagicKeyBattery.lnk'

function Stop-App {
    $running = Get-Process MagicKeyBattery -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        Start-Sleep -Seconds 1
    }
}

if ($Uninstall) {
    if (Test-Path $exe) { & $exe --startup off }
    Stop-App
    Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    Write-Host "Removed. Settings kept in $dest\MagicKeyBattery-data"
    return
}

# 1. Build a single-file Release exe
$publishDir = Join-Path $root 'bin\publish-install'
dotnet publish (Join-Path $root 'MagicKeyBattery.csproj') -c Release -r win-x64 --self-contained false -o $publishDir /p:PublishSingleFile=true /p:DebugType=none | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# 2. Copy into place (stop the running copy first — it locks the exe)
Stop-App
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir 'MagicKeyBattery.exe') -Destination $exe -Force

# 3. First install: carry over the settings/history from the development build if they exist
$data = Join-Path $dest 'MagicKeyBattery-data'
if (-not (Test-Path $data)) {
    $devData = Get-ChildItem -Path (Join-Path $root 'bin') -Recurse -Directory -Filter 'MagicKeyBattery-data' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($devData) { Copy-Item -LiteralPath $devData.FullName -Destination $data -Recurse }
}

# 4. Start Menu shortcut
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'Magic Keyboard battery, MX Keys layout and device switching'
$lnk.Save()

# 5. Start at sign-in (Task Scheduler logon task, replaces the old Run-key entry)
& $exe --startup on
if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not register the startup task' }

Write-Host "Installed to $exe"
if (-not $NoStart) { Start-Process $exe }
