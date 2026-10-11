<#
.SYNOPSIS
    Builds the Project-FREAK Windows installer (64-bit, unsigned).

.DESCRIPTION
    1. Publishes the app as a self-contained win-x64 Release build to ..\publish\win-x64.
    2. Checks that the LabJack redistributables are present in installer\redist.
    3. Runs makensis on Project-FREAK.nsi and writes installer\output\Project-FREAK-Setup-<version>.exe.

.PARAMETER NsisPath
    Full path to makensis.exe (NSIS 3).

.PARAMETER SkipPublish
    Reuse the existing ..\publish\win-x64 output instead of publishing again.

.EXAMPLE
    .\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$NsisPath = "C:\Program Files (x86)\NSIS\makensis.exe",
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent $installerDir
$project      = Join-Path $repoRoot 'Project-FREAK.csproj'
$publishDir   = Join-Path $repoRoot 'publish\win-x64'
$outputDir    = Join-Path $installerDir 'output'

if (-not $SkipPublish) {
    Write-Host 'Publishing win-x64 (self-contained, Release)...'
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $publishDir 'Project-FREAK.exe'))) {
    throw "Publish output not found: $publishDir"
}

$requiredRedist = @(
    (Join-Path $installerDir 'redist\LabJackBasic_2025-02-12.exe'),
    (Join-Path $installerDir 'redist\LabJacka64\dpinst64.exe'),
    (Join-Path $installerDir 'redist\LabJacka64\LabJacka64.inf')
)
foreach ($file in $requiredRedist) {
    if (-not (Test-Path $file)) { throw "Missing redistributable: $file" }
}

if (-not (Test-Path $NsisPath)) {
    throw "makensis not found at $NsisPath. Install NSIS 3 with: winget install --id NSIS.NSIS --exact"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host 'Compiling installer with makensis...'
Push-Location $installerDir
try {
    & $NsisPath Project-FREAK.nsi
    if ($LASTEXITCODE -ne 0) { throw "makensis failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

$setup = Get-ChildItem $outputDir -Filter 'Project-FREAK-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $setup) { throw "Installer was not created in $outputDir" }

Write-Host ("Built {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
