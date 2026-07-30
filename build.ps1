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

    -Publish additionally creates one GitHub release per built artifact and pushes the
    updated manifest.json. The order is not a convention but a constraint: a manifest
    entry whose release does not exist yet is a 404 in the user's dashboard, so the
    releases go first and the manifest only after the uploaded files have been
    downloaded again and their checksums confirmed.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Target net9.0
    ./build.ps1 -Changelog 'What changed.' -Publish
#>

[CmdletBinding()]
param(
    # Limit the build to one target framework. Default: all of them.
    [ValidateSet('net9.0', 'net10.0')]
    [string]$Target,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Shown in Jellyfin's plugin catalogue next to the version, and visible to anyone who
    # adds the repository - so English, like everything else in this project. It also has
    # to hold for BOTH Jellyfin lines, since one value is written to every target: word it
    # without naming a specific version number.
    [string]$Changelog = '',

    [string]$RepoOwner = 'MarcFauser',
    [string]$RepoName  = 'jellyfin-plugin-jflint',

    # Create the GitHub releases and push manifest.json. Without this the build stays
    # entirely local and nothing becomes visible to anyone.
    [switch]$Publish
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

# Runs a native command that is allowed to fail and returns its exit code. Needed because
# a profile may set $PSNativeCommandUseErrorActionPreference, which turns a non-zero exit
# into a terminating error and would take the "does this release exist" probe down with it.
function Invoke-Native([scriptblock]$Command)
{
    try
    {
        $null = & $Command 2>&1
        return $LASTEXITCODE
    }
    catch
    {
        return 1
    }
}

# --- Publish preconditions -----------------------------------------------------------
# Checked here, before the build, rather than next to the publishing code: every one of
# them is knowable up front, and a publish that is going to be refused must not leave a
# rewritten manifest.json behind. Found the hard way - a refused run had already replaced
# the changelog of a published version in the working copy.
if ($Publish)
{
    if ([string]::IsNullOrWhiteSpace($Changelog))
    {
        throw 'Publishing needs -Changelog: it is what the plugin catalogue shows next to the version.'
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue))
    {
        throw 'gh is not on PATH.'
    }

    if ((Invoke-Native { gh auth status }) -ne 0)
    {
        throw 'gh is not authenticated - run: gh auth login'
    }

    # The artifacts are stamped with the last commit that touched the plugin. If the source
    # has moved since, the ZIP about to be published corresponds to no commit at all, and
    # the next rebuild would produce a different file under the same version number.
    $dirty = @(git -C $root status --porcelain -- 'Jellyfin.Plugin.JFLint')
    if ($dirty.Count -gt 0)
    {
        throw "Uncommitted changes under Jellyfin.Plugin.JFLint. Commit them first, or the published ZIP matches no commit:`n  $($dirty -join "`n  ")"
    }

    # One version, one artifact. Replacing a published ZIP leaves every server that already
    # installed it holding the old file forever, while the manifest advertises a different
    # checksum under the same number - and Jellyfin only ever compares version numbers.
    foreach ($t in $targets)
    {
        if ((Invoke-Native { gh release view "v$($t.Version)" --repo "$RepoOwner/$RepoName" }) -eq 0)
        {
            throw "Release v$($t.Version) already exists. Raise the version in the project file rather than replacing a published artifact."
        }
    }
}

if (Test-Path -LiteralPath $distDir)
{
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $distDir

# Reproducible artifacts. The compiler already emits a byte-identical assembly (verified:
# two Release builds gave the same MD5), so the only variable parts were mine - the
# timestamp written into meta.json and the per-file times Compress-Archive stores. Both
# are pinned to the last commit that touched the plugin source, so rebuilding without a
# source change yields the same ZIP, the same MD5, and a published release stays valid.
# Deliberately not HEAD: committing the manifest or the README must not invalidate it.
$stampIso = git -C $root log -1 --format=%cI -- 'Jellyfin.Plugin.JFLint' 2>$null
if ([string]::IsNullOrWhiteSpace($stampIso))
{
    Write-Warning 'No commit found for the plugin source - using the current time. This build is not reproducible.'
    $stampUtc = [datetime]::UtcNow
}
else
{
    $stampUtc = [datetimeoffset]::Parse($stampIso).UtcDateTime
}
$timestamp = $stampUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')

Write-Host "JFLint  ($pluginId)  timestamp $timestamp" -ForegroundColor Cyan

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
        timestamp   = $timestamp
        assemblies  = @('Jellyfin.Plugin.JFLint.dll')
    }

    # Written without a BOM; Jellyfin's JSON reader chokes on one. Line endings are
    # left as PowerShell writes them - nothing here reads meta.json line by line.
    $metaJson = $meta | ConvertTo-Json -Depth 5
    $metaPath = Join-Path $stageDir 'meta.json'
    [System.IO.File]::WriteAllText($metaPath, $metaJson, [System.Text.UTF8Encoding]::new($false))

    # Compress-Archive stores each entry's last-write time, so without this the ZIP would
    # differ on every run even though its contents are identical.
    Get-ChildItem -LiteralPath $stageDir -File | ForEach-Object { $_.LastWriteTimeUtc = $stampUtc }

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
    # Check the shape on the way in as well. A malformed manifest would otherwise be
    # carried into the next build and fail somewhere further down with a confusing error.
    # @() is required: ConvertFrom-Json unrolls a one-element array into a bare object.
    # It also turns a doubly nested [[{...}]] into an array whose first element is itself
    # an array - which is exactly what the type test below catches.
    $loaded = @(ConvertFrom-Json -InputObject (Get-Content -LiteralPath $manifestPath -Raw))
    if ($loaded[0] -isnot [System.Management.Automation.PSCustomObject])
    {
        throw "$manifestPath is not a flat JSON array of package objects. Delete it to start over."
    }

    $package = $loaded[0]
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

# Optional catalogue tile. Any raster or vector format works - Jellyfin passes imageUrl
# straight into an <img>, and raw.githubusercontent.com serves .svg as image/svg+xml
# (measured), not as text/plain. Drop a logo.* next to this script and it is picked up.
$logo = Get-ChildItem -LiteralPath $root -File |
    Where-Object { $_.Name -match '^logo\.(png|jpg|jpeg|webp|svg)$' } |
    Sort-Object Name | Select-Object -First 1

if ($logo)
{
    $imageUrl = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/main/$($logo.Name)"
    $package | Add-Member -NotePropertyName imageUrl -NotePropertyValue $imageUrl -Force
    Write-Host "  Logo: $($logo.Name)" -ForegroundColor DarkGray
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
        timestamp = $timestamp
    }
}

# Highest version first - that is the order Jellyfin picks from after the ABI filter.
$package.versions = @($kept + $fresh | Sort-Object { [version]$_.version } -Descending)

# Jellyfin deserialises the manifest into PackageInfo[]. Anything but a flat array of
# package objects throws a JsonException that InstallationManager swallows - the plugin
# then simply never appears in the catalogue, with no visible error.
#
# Getting this shape right needs care; all three wrong ways were tried against a live
# server first. Measured 2026-07-29:
#   ,$package | ConvertTo-Json                     -> {...}     the pipeline unrolls again
#   ConvertTo-Json -InputObject @($p) -AsArray     -> [[{...}]] -AsArray wraps a second time
#   ConvertTo-Json -InputObject @($p)              -> [{...}]   correct, also for 2+ packages
$manifestJson = ConvertTo-Json -InputObject @($package) -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

# --- Checks -------------------------------------------------------------------------
# Everything below reproduces what Jellyfin does with these files. Each of these once
# failed for real, and every one of them failed *silently* - the plugin simply did not
# show up, or the install aborted. Hence assertions rather than trust.
Write-Host ""
Write-Host "Checks" -ForegroundColor Cyan

# 1. Shape. A check for a leading '[' is not enough - '[[' passes that too, and that is
#    exactly the mistake that happened. Note the @(): without it ConvertFrom-Json unrolls
#    a one-element array into a bare object and the check would reject a good manifest.
$probe = @(ConvertFrom-Json -InputObject $manifestJson)
if ($probe[0] -isnot [System.Management.Automation.PSCustomObject] -or
    -not $probe[0].PSObject.Properties['guid'] -or
    -not $probe[0].PSObject.Properties['versions'])
{
    throw 'manifest.json must be a flat JSON array of package objects, not a bare object or a nested array.'
}
Write-Host "  ok  manifest is a flat array of package objects"

# 2. The guid must parse - PackageInfo.Id is a Guid, not a string.
if (-not [guid]::TryParse($probe[0].guid, [ref][guid]::Empty))
{
    throw "manifest guid is not a valid GUID: $($probe[0].guid)"
}

# 3. Every version and targetAbi must parse as a Version. VersionInfo.Version does
#    Version.Parse in its setter, so a bad value takes the whole manifest down.
foreach ($v in $probe[0].versions)
{
    foreach ($field in 'version', 'targetAbi')
    {
        if (-not [version]::TryParse($v.$field, [ref]([version]'0.0')))
        {
            throw "manifest entry has an unparsable $field : $($v.$field)"
        }
    }
}
Write-Host "  ok  $($probe[0].versions.Count) version(s), all version/targetAbi parsable"

# 4. No version number twice. After the ABI filter Jellyfin takes the highest version;
#    duplicates would be decided by array order alone, and an upgrading server would
#    never be offered the matching build.
$duplicates = $probe[0].versions | Group-Object version | Where-Object Count -gt 1
if ($duplicates)
{
    throw "version $($duplicates[0].Name) appears $($duplicates[0].Count) times in the manifest."
}

# 5. Checksums must match the artifacts just built. Jellyfin verifies the MD5 after
#    downloading and aborts with InvalidDataException on a mismatch.
foreach ($t in $targets)
{
    $entry  = $probe[0].versions | Where-Object version -eq $t.Version
    $file   = Join-Path $distDir $t.ZipName
    $actual = (Get-FileHash -LiteralPath $file -Algorithm MD5).Hash.ToLowerInvariant()
    if ($entry.checksum -ne $actual)
    {
        throw "checksum mismatch for $($t.ZipName): manifest $($entry.checksum), file $actual"
    }
    if (-not $entry.sourceUrl.EndsWith("/$($t.ZipName)"))
    {
        throw "sourceUrl for $($t.Version) does not point at $($t.ZipName)"
    }
}
Write-Host "  ok  checksums and sourceUrl file names match the artifacts"

Write-Host ""
Write-Host "Artifacts in $distDir" -ForegroundColor Cyan
Write-Host "manifest.json updated - $($package.versions.Count) version(s) listed" -ForegroundColor Cyan

# --- Publish -------------------------------------------------------------------------
# Two halves that cannot be rolled back independently: a release nobody's manifest names
# is merely invisible, but a manifest entry without its release is a failed download in
# the user's dashboard. So everything knowable is checked before anything becomes
# visible, the releases go first, and the manifest follows only once the uploaded files
# have been fetched back and hashed.
if (-not $Publish)
{
    Write-Host ""
    Write-Host "Nothing published. Add -Publish to create the releases and push the manifest." -ForegroundColor DarkGray
    return
}

Write-Host ""
Write-Host "Publish" -ForegroundColor Cyan

foreach ($t in $targets)
{
    $zip = Join-Path $distDir $t.ZipName
    gh release create "v$($t.Version)" $zip --repo "$RepoOwner/$RepoName" --title "v$($t.Version)" --notes $Changelog
    if ($LASTEXITCODE -ne 0)
    {
        throw "gh release create failed for v$($t.Version) - no manifest was pushed, so nothing is advertised that does not exist."
    }
}

# Download what the world now sees. Hashing the local file again would prove nothing
# about the upload, and this is exactly what Jellyfin does before it installs.
$verifyDir = Join-Path $root 'tmp/publish-verify'
$null = New-Item -ItemType Directory -Path $verifyDir -Force
try
{
    foreach ($t in $targets)
    {
        $entry = $probe[0].versions | Where-Object version -eq $t.Version
        $copy  = Join-Path $verifyDir $t.ZipName
        Invoke-WebRequest -Uri $entry.sourceUrl -OutFile $copy
        $actual = (Get-FileHash -LiteralPath $copy -Algorithm MD5).Hash.ToLowerInvariant()
        if ($actual -ne $entry.checksum)
        {
            throw "the published v$($t.Version) hashes $actual, the manifest says $($entry.checksum)"
        }
        Write-Host "  ok  v$($t.Version) fetched back from the release, md5 matches the manifest"
    }
}
finally
{
    Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Only manifest.json - never a blanket stage, which would sweep up whatever else happens
# to be lying in the working directory.
git -C $root add manifest.json
git -C $root diff --cached --quiet
if ($LASTEXITCODE -eq 0)
{
    Write-Host "  manifest.json unchanged - nothing to commit"
}
else
{
    git -C $root commit -m "Release $(($targets.Version) -join ' / ')"
    if ($LASTEXITCODE -ne 0)
    {
        throw 'git commit failed - the releases exist but the manifest is not committed.'
    }
}

# The credential helper is supplied per invocation rather than configured globally: gh is
# already authenticated (checked above), and this leaves the user's git config alone.
git -C $root -c credential.helper='!gh auth git-credential' push origin HEAD
if ($LASTEXITCODE -ne 0)
{
    throw 'git push failed - the releases exist but the manifest is not published yet. Push manually.'
}

Write-Host "  ok  manifest pushed" -ForegroundColor Green
Write-Host ""
Write-Host "Jellyfin reads: https://raw.githubusercontent.com/$RepoOwner/$RepoName/main/manifest.json" -ForegroundColor DarkGray
