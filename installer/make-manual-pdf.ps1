# Builds docs\Wave_Flume_User_Manual.pdf from docs\USER_MANUAL.md (with the screenshots in docs\screenshots).
# Needs the .NET SDK and Microsoft Edge. Run from the repository root:  powershell -File installer\make-manual-pdf.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $root 'tools\md2html'
$version = '1.0'
$md = Join-Path $root 'docs\USER_MANUAL.md'
$logo = Join-Path $root 'src\WaveFlume.App\Assets\WaveFlume.png'
$html = Join-Path $root 'docs\manual_build.html'
$pdf = Join-Path $root 'docs\Wave_Flume_User_Manual.pdf'

dotnet build $tool -c Release -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Could not build tools\md2html.' }
dotnet (Join-Path $tool 'bin\Release\net8.0\md2html.dll') $md $html $logo $version
if ($LASTEXITCODE -ne 0) { throw 'Markdown conversion failed.' }

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }
if (Test-Path $pdf) { Remove-Item $pdf -Force }
Start-Process $edge -ArgumentList @('--headless', '--disable-gpu', '--no-pdf-header-footer', "--print-to-pdf=$pdf", ([Uri]$html).AbsoluteUri) -Wait -NoNewWindow
Remove-Item $html -Force
if (-not (Test-Path $pdf)) { throw 'The PDF was not created.' }
"Wrote $pdf"
