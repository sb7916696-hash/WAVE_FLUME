# Builds a Wave Flume release: the PDF manual, the tests, the self-contained single-file app, plctag.dll, and the
# installer (needs Inno Setup 6). Run from the repository root:  powershell -File installer\build-release.ps1
# Output: artifacts\publish (the app) and artifacts\installer\WaveFlume_Setup_<version>.exe (the installer).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'

# The Microsoft Visual C++ runtime installer is bundled in the setup program (from https://aka.ms/vs/17/release/vc_redist.x64.exe).
$redist = Join-Path $PSScriptRoot 'prereq\vc_redist.x64.exe'
if (-not (Test-Path $redist)) {
    throw "Missing $redist. Download https://aka.ms/vs/17/release/vc_redist.x64.exe into installer\prereq first."
}

& (Join-Path $PSScriptRoot 'make-manual-pdf.ps1')

dotnet test (Join-Path $root 'WaveFlume.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release built.' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\WaveFlume.App') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# libplctag embeds its native library and extracts it next to the exe; do it now so the installer ships it.
Start-Process -FilePath (Join-Path $publish 'WaveFlume.exe') -ArgumentList '--extract-native' -Wait
if (-not (Test-Path (Join-Path $publish 'plctag.dll'))) { throw 'plctag.dll was not extracted.' }

# The documents travel with the app.
New-Item -ItemType Directory -Force (Join-Path $publish 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\Wave_Flume_User_Manual.pdf'), (Join-Path $root 'docs\PLC_IO_MAP.md') (Join-Path $publish 'docs') -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc (Join-Path $PSScriptRoot 'WaveFlume.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
    Write-Host "Installer: $(Join-Path $root 'artifacts\installer')"
} else {
    Write-Host "Inno Setup 6 not found; published files are in $publish. Install it (winget install JRSoftware.InnoSetup) and rerun to build the installer."
}
