# Builds Brightness Mirror. No SDK needed: it uses the C# compiler that ships with Windows
# (.NET Framework 4.x) and, for the installer, Inno Setup 6 (https://jrsoftware.org/isinfo.php).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1               # exe + installer
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstaller  # exe only
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Icon         # also redraw assets\app.ico
#
# Output: build\BrightnessMirror.exe and build\BrightnessMirror-Setup-<version>.exe
param([switch]$NoInstaller, [switch]$Icon)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'build'
$src  = Join-Path $root 'src\BrightnessMirror.cs'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
New-Item -ItemType Directory -Force -Path $out | Out-Null

if ($Icon) {
    & $csc /nologo /target:exe /main:BrightnessMirror.MakeIconTool @refs "/out:$out\MakeIcon.exe" $src (Join-Path $root 'tools\MakeIcon.cs')
    if ($LASTEXITCODE) { throw 'Icon tool build failed.' }
    & "$out\MakeIcon.exe" (Join-Path $root 'assets\app.ico')
}

$version = [regex]::Match((Get-Content $src -Raw), 'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
& $csc /nologo /target:winexe /optimize+ @refs "/win32icon:$root\assets\app.ico" "/out:$out\BrightnessMirror.exe" $src
if ($LASTEXITCODE) { throw 'Compile failed.' }
Write-Output "Built build\BrightnessMirror.exe ($version)"
if ($NoInstaller) { return }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found, so no installer was built. Get it from https://jrsoftware.org/isinfo.php'
    return
}
& $iscc /Q "/DAppVersion=$version" "/O$out" (Join-Path $root 'installer\BrightnessMirror.iss')
if ($LASTEXITCODE) { throw 'Installer build failed.' }
Write-Output "Built build\BrightnessMirror-Setup-$version.exe"
