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
    [string]$Configuration = 'Release',

    # Shown in Jellyfin's plugin catalogue next to the version.
    [string]$Changelog = '',

    [string]$RepoOwner = 'MarcFauser',
    [string]$RepoName  = 'jellyfin-plugin-jflint'
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

# Single source of truth for the versions and the id: the project file and Plugin.cs.
# The version differs per target framework - its major encodes the Jellyfin line - so it
# is read from the PropertyGroup carrying the matching TargetFramework condition.
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
foreach ($t in $targets)
{
    # GetAttribute returns '' when the attribute is absent; reading .Condition directly
    # would throw under StrictMode on the unconditional PropertyGroup.
    $group = $projectXml.Project.PropertyGroup |
        Where-Object { $_.GetAttribute('Condition') -match [regex]::Escape("'$($t.Framework)'") }

    $v = @($group.Version) | Where-Object { $_ } | Select-Object -First 1
    if (-not $v)
    {
        throw "No <Version> found for $($t.Framework) in $project"
    }

    $t | Add-Member -NotePropertyName Version -NotePropertyValue $v
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
$null = New-Item -ItemType Directory -Path $distDir

Write-Host "JFLint  ($pluginId)" -ForegroundColor Cyan

foreach ($t in $targets)
{
    Write-Host ""
    Write-Host "=== $($t.Framework) -> $($t.Line), Version $($t.Version) ===" -ForegroundColor Cyan

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
        version     = $t.Version
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

    # The version already identifies the Jellyfin line, so the file name needs no ABI part.
    $zipName = "jellyfin-plugin-jflint_$($t.Version).zip"
    $zip     = Join-Path $distDir $zipName
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zip -Force
    Remove-Item -LiteralPath $stageDir -Recurse -Force

    # Jellyfin verifies this MD5 against the downloaded file and aborts the install on a
    # mismatch (InstallationManager: MD5.HashDataAsync -> InvalidDataException).
    $t | Add-Member -NotePropertyName Checksum -NotePropertyValue (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
    $t | Add-Member -NotePropertyName ZipName  -NotePropertyValue $zipName

    $size = (Get-Item -LiteralPath $zip).Length
    Write-Host ("  {0}  ({1:N0} bytes, md5 {2})" -f $zipName, $size, $t.Checksum) -ForegroundColor Green
}

# --- Repository manifest -----------------------------------------------------------
# Jellyfin reads this from a URL added under Dashboard -> Plugins -> Repositories and
# downloads sourceUrl from it. Shape per MediaBrowser.Model/Updates/{PackageInfo,VersionInfo}.
# Existing entries are kept: the manifest is the release history, not a snapshot.
$manifestPath = Join-Path $root 'manifest.json'

if (Test-Path -LiteralPath $manifestPath)
{
    $package = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)[0]
}
else
{
    $package = [PSCustomObject]@{
        guid        = $pluginId
        name        = 'JFLint'
        description = 'Adds endpoints for library defects that /Items cannot filter for, ' +
                      'starting with episodes whose season could not be determined.'
        overview    = 'Library-lint queries the Jellyfin API cannot express.'
        owner       = $RepoOwner
        category    = 'General'
        versions    = @()
    }
}

$logo = Join-Path $root 'logo.png'
if (Test-Path -LiteralPath $logo)
{
    $imageUrl = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/main/logo.png"
    $package | Add-Member -NotePropertyName imageUrl -NotePropertyValue $imageUrl -Force
}

# Keep every version that is not being rebuilt right now, then add the fresh ones.
$rebuilt = $targets.Version
$kept    = @($package.versions | Where-Object { $rebuilt -notcontains $_.version })

$fresh = foreach ($t in $targets)
{
    [PSCustomObject]@{
        version   = $t.Version
        targetAbi = $t.TargetAbi
        sourceUrl = "https://github.com/$RepoOwner/$RepoName/releases/download/v$($t.Version)/$($t.ZipName)"
        checksum  = $t.Checksum
        changelog = $Changelog
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
}

# Highest version first - that is the order Jellyfin picks from after the ABI filter.
$package.versions = @($kept + $fresh | Sort-Object { [version]$_.version } -Descending)

$manifestJson = ,$package | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Artifacts in $distDir" -ForegroundColor Cyan
Write-Host "manifest.json updated - $($package.versions.Count) version(s) listed" -ForegroundColor Cyan
Write-Host ""
Write-Host "Publish a build:  gh release create v<version> dist\<zip> --title v<version>" -ForegroundColor DarkGray
Write-Host "Then commit manifest.json - Jellyfin reads it from the raw URL." -ForegroundColor DarkGray
