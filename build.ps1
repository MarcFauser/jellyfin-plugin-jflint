#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Builds Jellyfin.Plugin.JFLint and packages one installable ZIP per Jellyfin line.

.DESCRIPTION
    Publishes the plugin for every target framework, writes the meta.json that
    Jellyfin's PluginManager reads from the plugin folder, and packs both into a ZIP
    under dist\.

    net9.0  -> Jellyfin 10.11.x   (the .NET version is dictated by the server runtime)
    net10.0 -> Jellyfin 12.x      (compiles, but untested - no v12 server here yet)

    Install a ZIP by extracting it into <ProgramDataPath>/plugins/JFLint_<version>/
    on the server and restarting Jellyfin. The server's ProgramDataPath is shown by
    GET /System/Info.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Target net9.0
#>

[CmdletBinding()]
param(
    # Limit the build to one target framework. Default: all of them.
    [ValidateSet('net9.0', 'net10.0')]
    [string]$Target,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$projectDir = Join-Path $root 'Jellyfin.Plugin.JFLint'
$project    = Join-Path $projectDir 'Jellyfin.Plugin.JFLint.csproj'
$distDir    = Join-Path $root 'dist'

# Which Jellyfin line each target framework serves. targetAbi is compared as a Version
# by the server: the plugin loads when the server version is >= this value.
$targets = @(
    [PSCustomObject]@{ Framework = 'net9.0';  TargetAbi = '10.11.0.0'; Line = 'Jellyfin 10.11' }
    [PSCustomObject]@{ Framework = 'net10.0'; TargetAbi = '12.0.0.0';  Line = 'Jellyfin 12' }
)

if ($Target)
{
    $targets = @($targets | Where-Object Framework -eq $Target)
}

# Single source of truth for version and id: the project file and Plugin.cs.
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version    = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version)
{
    throw "No <Version> found in $project"
}

$pluginSource = Get-Content -LiteralPath (Join-Path $projectDir 'Plugin.cs') -Raw
if ($pluginSource -notmatch 'Guid\.Parse\("([0-9a-fA-F-]{36})"\)')
{
    throw 'Could not read the plugin GUID from Plugin.cs'
}
$pluginId = $Matches[1]

if (Test-Path -LiteralPath $distDir)
{
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distDir | Out-Null

Write-Host "JFLint $version  ($pluginId)" -ForegroundColor Cyan

foreach ($t in $targets)
{
    Write-Host ""
    Write-Host "=== $($t.Framework) -> $($t.Line) ===" -ForegroundColor Cyan

    $stageDir = Join-Path $distDir $t.Framework
    dotnet publish $project -c $Configuration -f $t.Framework -o $stageDir --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for $($t.Framework)"
    }

    # The server ships the Jellyfin assemblies itself (ExcludeAssets="runtime"), so only
    # our own DLL may travel. Anything else would shadow the server's copy.
    Get-ChildItem -LiteralPath $stageDir -File |
        Where-Object { $_.Name -notlike 'Jellyfin.Plugin.JFLint.*' } |
        Remove-Item -Force

    $meta = [ordered]@{
        guid        = $pluginId
        name        = 'JFLint'
        overview    = 'Library-lint queries the Jellyfin API cannot express.'
        description = 'Adds endpoints for library defects that /Items cannot filter for, ' +
                      'starting with episodes whose season could not be determined.'
        owner       = 'marc.fauser'
        category    = 'General'
        version     = $version
        targetAbi   = $t.TargetAbi
        # 0 = PluginStatus.Active
        status      = 0
        autoUpdate  = $false
        changelog   = ''
        timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        assemblies  = @('Jellyfin.Plugin.JFLint.dll')
    }

    # Written without a BOM; Jellyfin's JSON reader chokes on one. Line endings are
    # left as PowerShell writes them - nothing here reads meta.json line by line.
    $metaJson = $meta | ConvertTo-Json -Depth 5
    $metaPath = Join-Path $stageDir 'meta.json'
    [System.IO.File]::WriteAllText($metaPath, $metaJson, [System.Text.UTF8Encoding]::new($false))

    $zip = Join-Path $distDir "jellyfin-plugin-jflint_$($version)_abi$($t.TargetAbi).zip"
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zip -Force
    Remove-Item -LiteralPath $stageDir -Recurse -Force

    $size = (Get-Item -LiteralPath $zip).Length
    Write-Host ("  {0}  ({1:N0} bytes)" -f (Split-Path $zip -Leaf), $size) -ForegroundColor Green
}

Write-Host ""
Write-Host "Artifacts in $distDir" -ForegroundColor Cyan
