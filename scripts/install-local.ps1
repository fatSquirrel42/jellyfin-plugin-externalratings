<#
.SYNOPSIS
    Build the External Ratings plugin and install it into a local Jellyfin instance for testing.

.DESCRIPTION
    Builds src/Jellyfin.Plugin.ExternalRatings and copies the plugin DLL (+ PDB) and meta.json into
    <JellyfinDataDir>/plugins/External Ratings/. Restart Jellyfin afterwards to load the new build.

.PARAMETER Configuration
    Build configuration: Debug (default) or Release.

.PARAMETER JellyfinDataDir
    Root data directory of the Jellyfin instance (the folder that contains 'plugins', 'config', ...).
    Defaults to %LOCALAPPDATA%\jellyfin. Point this at YOUR running instance if it differs.

.EXAMPLE
    ./scripts/install-local.ps1

.EXAMPLE
    ./scripts/install-local.ps1 -Configuration Release -JellyfinDataDir 'D:\JellyfinData'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$JellyfinDataDir = (Join-Path $env:LOCALAPPDATA 'jellyfin')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Jellyfin.Plugin.ExternalRatings/Jellyfin.Plugin.ExternalRatings.csproj'
$pluginFolderName = 'External Ratings'

Write-Host "Building plugin ($Configuration)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }

$outDir = Join-Path $repoRoot "src/Jellyfin.Plugin.ExternalRatings/bin/$Configuration/net9.0"

if (-not (Test-Path $JellyfinDataDir)) {
    throw "Jellyfin data dir not found: '$JellyfinDataDir'. Pass -JellyfinDataDir <path> pointing at your instance's data folder (the one containing 'plugins')."
}

$target = Join-Path $JellyfinDataDir "plugins/$pluginFolderName"
New-Item -ItemType Directory -Force -Path $target | Out-Null

# DLL + meta.json are required; the PDB is copied when present (helpful for debugging, optional).
$required = @('Jellyfin.Plugin.ExternalRatings.dll', 'meta.json')
$optional = @('Jellyfin.Plugin.ExternalRatings.pdb')

foreach ($name in $required) {
    $src = Join-Path $outDir $name
    if (-not (Test-Path $src)) { throw "Required build artifact missing: '$src'." }
    Copy-Item -Path $src -Destination $target -Force
}

foreach ($name in $optional) {
    $src = Join-Path $outDir $name
    if (Test-Path $src) { Copy-Item -Path $src -Destination $target -Force }
}

Write-Host "Installed to: $target" -ForegroundColor Green
Write-Host 'Restart Jellyfin to load the new build.' -ForegroundColor Yellow
