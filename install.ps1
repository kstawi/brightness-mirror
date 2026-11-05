# Builds Brightness Mirror from source and installs it for the current user only, without the
# installer: to %LOCALAPPDATA%\Programs\BrightnessMirror, switched on in startup apps, and started.
# Most people should use the installer from the Releases page instead.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1             # install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  # remove everything
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$name = 'BrightnessMirror'
$dest = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe  = Join-Path $dest "$name.exe"

# Stop a running copy so the exe can be replaced or removed.
Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name $name -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\$name" -Recurse -ErrorAction SilentlyContinue      # remembered paused state
    Remove-Item $dest, (Join-Path $env:LOCALAPPDATA $name) -Recurse -Force -ErrorAction SilentlyContinue  # app, settings, log
    Write-Output "Uninstalled $name."
    return
}

& (Join-Path $PSScriptRoot 'build.ps1') -NoInstaller
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot "build\$name.exe") $dest -Force
Start-Process -FilePath $exe -ArgumentList '--startup', 'on' -Wait   # add to startup apps
Start-Process -FilePath $exe
Write-Output "Installed to $dest, added to startup apps and started."
