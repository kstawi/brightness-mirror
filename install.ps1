# Builds BrightnessMirror from source, installs it to %LOCALAPPDATA%\Programs\BrightnessMirror,
# adds it to startup (Settings > Apps > Startup) and starts it.
# Uses the C# compiler that ships with Windows (.NET Framework 4.x), so no SDK is needed.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1             # install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  # remove everything
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$name = 'BrightnessMirror'
$src  = Join-Path $PSScriptRoot 'src'
$dest = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe  = Join-Path $dest "$name.exe"
$run  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Stop a running copy so the exe can be replaced or removed.
Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty -Path $run -Name $name -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\$name" -Recurse -ErrorAction SilentlyContinue   # remembered paused state
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Write-Output "Uninstalled $name."
    return
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
& $csc /nologo /target:winexe /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/out:$exe" (Join-Path $src "$name.cs")
if ($LASTEXITCODE -ne 0) { throw "Compile failed." }

# Keep existing settings on update.
if (-not (Test-Path (Join-Path $dest "$name.ini"))) {
    Copy-Item (Join-Path $src "$name.ini") $dest
}

Set-ItemProperty -Path $run -Name $name -Value "`"$exe`""
Start-Process -FilePath $exe
Write-Output "Installed to $dest, added to startup and started."
