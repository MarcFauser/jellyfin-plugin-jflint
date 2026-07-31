# Jellyfin.Plugin.JFLint

A small Jellyfin server plugin that answers library-lint questions the stock API
**cannot express**.

The first one: *which episodes has Jellyfin failed to assign a season to?* In API terms
that is `ParentIndexNumber IS NULL`, and `/Items` cannot ask it - its
`parentIndexNumber` filter is an `int?`, where `null` means "do not filter". A client
therefore has to fetch the entire episode list and filter locally. In this library that
is 25,440 items fetched for **6** hits, and it costs ~47 s.

See [HISTORY.md](HISTORY.md) for why the plugin exists and
[ARCHITECTURE.md](ARCHITECTURE.md) for how it works.

## Endpoints

Every route requires an authenticated **administrator** (`RequiresElevation`) because the
responses contain media file paths.

### Episodes without a season

| Route | How it queries |
|---|---|
| `GET /JFLint/EpisodesWithoutSeason` | via `ILibraryManager` - only public, promised interfaces |
| `GET /JFLint/EpisodesWithoutSeasonDB` | straight from `JellyfinDbContext` - the filter runs as SQL |

Both return the same shape:

```json
[
  {
    "id": "…", "seriesId": "…", "seriesName": "…",
    "indexNumber": 3, "name": "…", "path": "/…/Series/…/file.mkv"
  }
]
```

They exist side by side on purpose: each is the other's cross-check. If the two return
different sets, the database query is wrong.

```powershell
$h = @{ 'X-Emby-Token' = $apiKey }
Invoke-RestMethod "$ServerUrl/JFLint/EpisodesWithoutSeasonDB" -Headers $h
```

### Library layout

Five more findings, each again on two routes - `X` via `ILibraryManager`, `XDB` straight
off the database. Answering these over the stock API costs ~22 s, because "did this folder
yield any file at all" is a question about *absent* rows, so every episode has to cross
the wire.

| Route pair | Finds |
|---|---|
| `PhantomSeason` | a season folder with no readable number that does hold files - typically a per-episode release folder mistaken for a season |
| `SeasonFolderWithoutVideo` | a season folder holding no playable episode, whatever its number |
| `DuplicateSeasonNumber` | two or more seasons of one series sharing a number; every member of the group is returned |
| `SeriesWithoutFiles` | a series folder Jellyfin read no playable file from, though the files are there |
| `OrphanedItem` | a season or episode pointing at a series, season or parent row that no longer exists |

All ten return the same shape, so one parser serves them all. Fields that do not apply to
a kind are omitted, not sent as null:

```json
[
  {
    "Kind": "SeriesWithoutFiles",
    "ItemId": "05ed59f206f57e57762a48315b65f1d2",
    "ItemType": "Series",
    "Name": "9-1-1",
    "Path": "/…/Series/1080p/9-1-1.S02.…",
    "EpisodeRowCount": 78
  }
]
```

`SeasonNumber` and `GroupSize` are filled for `DuplicateSeasonNumber`, `EpisodeRowCount`
for `SeriesWithoutFiles`, `DanglingLink` and `DanglingId` for `OrphanedItem`. Everything
is typed - no prose, no numbers inside strings - because the caller composes the sentences
the user sees from its own language files.

Rows come back ordered by series name, then season number, then name, nulls last. The
order is identical on both routes of a pair, which is what makes comparing them a
one-liner:

```powershell
$a = Invoke-RestMethod "$ServerUrl/JFLint/PhantomSeason"   -Headers $h
$b = Invoke-RestMethod "$ServerUrl/JFLint/PhantomSeasonDB" -Headers $h
Compare-Object $a.ItemId $b.ItemId    # nothing means the two agree
```

Two definitions worth knowing, both of which look like details and are not:

- **A "real" episode is one with `IsVirtualItem == false`.** The virtual rows the Missing
  Episode Fetcher creates must never count as content, or every finding above evaporates.
- **An unset link is `Guid.Empty`, not null.** Jellyfin normalises `ParentId` to `NULL`
  but writes `SeriesId` and `SeasonId` raw from non-nullable sources. `OrphanedItem`
  treats all-zeroes as "not set"; without that it would report every seasonless episode.

### Items at a path

```
GET /JFLint/ItemsByPath?path=<path>          via ILibraryManager
GET /JFLint/ItemsByPathDB?path=<path>        straight from the database
```

The odd one out: a **lookup**, not a finding. It takes a parameter, and it covers **every**
item type rather than just TV. Returns the item at that path plus everything beneath it.

It exists because `/Items` treats `Path` as an output field only. A caller holding a path
and needing an item id - which is all `/Items/{id}/Refresh` and `DELETE /Items/{id}`
accept - otherwise has to read the whole item list and match locally: ~7 s and ~50,000
rows to identify one item. Searching by name is no substitute, because the case where the
id is needed most is a wrong metadata match, and then the name has nothing to do with the
file name.

```json
[
  { "Id": "…", "ItemType": "Movie", "Name": "…", "Path": "/…/Ring (2002).mkv" }
]
```

Three properties of the contract worth relying on:

- **Matching is ordinal and case-sensitive**, following SQLite's BINARY collation and the
  case-sensitive file systems Jellyfin usually runs on.
- **A path that holds nothing is `200` with `[]`**, never `404`. `404` means the plugin is
  absent, which is what lets a caller fall through to its own slower path.
- **The prefix is anchored on the separator**, so `/Movies/Ring` never matches
  `/Movies/Ring2`. A trailing separator on the input is trimmed; a bare root is rejected
  with `400` rather than returning the entire library.

## Consuming them: the fallback chain

A client should try the routes fastest-first and keep the old code path as the last
resort, so it still works against a server where this plugin is absent, disabled, or no
longer matches the Jellyfin version:

```
1. GET /JFLint/EpisodesWithoutSeasonDB     28 ms   - straight from the database
2. GET /JFLint/EpisodesWithoutSeason        2.9 s  - via ILibraryManager
3. fetch every episode, filter client-side  25 s+  - works on any Jellyfin
```

**Two ways to get this wrong, both of which fail quietly:**

- **An empty result is not a failure.** `200` with `[]` means the library is clean -
  that is the answer we want, not a reason to fall through to the 25-second scan. Only a
  `404` (route not there) or a `5xx` (route there but broken, e.g. the database schema
  moved under a new Jellyfin version) may advance the chain.
- **`401`/`403` must abort, not fall through.** A wrong or expired API key would
  otherwise silently push every run onto the slowest path, and the tool would just seem
  to have got slower.

```powershell
function Get-EpisodesWithoutSeason
{
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ServerUrl, [Parameter(Mandatory)][hashtable]$Headers)

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    foreach ($route in 'JFLint/EpisodesWithoutSeasonDB', 'JFLint/EpisodesWithoutSeason')
    {
        try
        {
            $hits = Invoke-RestMethod -Uri "$ServerUrl/$route" -Headers $Headers
            Write-Verbose "JFLint: $route"
            return , @($hits)   # the leading comma stops a one-element list being unrolled
        }
        catch
        {
            $code = $_.Exception.Response.StatusCode.value__

            if ($code -ge 500)
            {
                Write-Warning "$route answered $code - the plugin probably no longer matches this Jellyfin version. Falling through to the next route."
            }
            elseif ($code -ne 404)
            {
                throw   # 401/403 and everything else: do NOT fall through, or it just gets quietly slow
            }
        }
    }

    Write-Verbose 'JFLint unavailable - using the legacy path.'
    return , @(Get-EpisodesWithoutSeasonLegacy -ServerUrl $ServerUrl -Headers $Headers)
}
```

Worth logging which route answered. If step 1 starts warning, that is the signal to
rebuild the plugin for the new Jellyfin line - the tool keeps working meanwhile, just
slower, and nothing about the result changes.

## Requirements

- Jellyfin **10.11.x** (built against 10.11.11) or **12.x** (built against 12.0.0-rc3,
  **compiled but untested** - no v12 server available here yet)
- .NET SDK to build. No .NET 9 SDK required even for the `net9.0` output; the reference
  packs are restored from NuGet.

## Build

```powershell
./build.ps1                 # both Jellyfin lines
./build.ps1 -Target net9.0  # just 10.11
```

This publishes the plugin, writes the `meta.json` that Jellyfin's plugin manager reads,
and packs one ZIP per line into `dist\`:

```
dist\jellyfin-plugin-jflint_1.0.0_abi10.11.0.0.zip
dist\jellyfin-plugin-jflint_1.0.0_abi12.0.0.0.zip
```

`dotnet build` is used throughout - no MSBuild, no Visual Studio.

**The build is reproducible.** Rebuilding without a source change produces byte-identical
ZIPs, so a published release keeps matching the checksum in `manifest.json`. That is not
free: the compiler is deterministic by itself, but the timestamp in `meta.json` and the
per-file times stored inside the ZIP are pinned to the last commit that touched
`Jellyfin.Plugin.JFLint/` - deliberately not `HEAD`, so committing the manifest or this
README does not invalidate the artifacts.

`build.ps1` ends with a block of checks that reproduce what Jellyfin does with these
files: manifest shape, GUID, every `version`/`targetAbi` parsable, no duplicate version,
and each `checksum` matched against the ZIP on disk. Each of those once failed for real,
and each failed *silently*.

## Publishing

```powershell
./build.ps1 -Changelog "what changed" -Publish
```

One command, because the order is a constraint rather than a convention: **a manifest
entry whose release does not exist yet is a failed download in the user's dashboard.** So
`-Publish` creates the GitHub releases first, fetches each uploaded ZIP back and checks
its MD5 against the manifest - which is exactly what Jellyfin does before installing -
and only then commits and pushes `manifest.json`.

It refuses to start, *before* building anything, when:

- `-Changelog` is empty; it is what the catalogue shows next to the version
- `gh` is missing or not authenticated
- anything under `Jellyfin.Plugin.JFLint/` is uncommitted - the ZIP is stamped with the
  last commit that touched the plugin, so otherwise the published file matches no commit
- a release for one of the versions already exists - one version, one artifact

The checks run up front rather than next to the publishing code so that a run which is
going to be refused does not leave a rewritten `manifest.json` behind. That is not
hypothetical: it happened while this was being written.

Without `-Publish` the build stays entirely local and says so.

## Versions

The major version says which Jellyfin line a build belongs to:

| Plugin version | Jellyfin | targetAbi |
|---|---|---|
| `11.x.x.x` | 10.11.x | `10.11.0.0` |
| `12.x.x.x` | 12.x | `12.0.0.0` |

**One version, one artifact.** Never replace a published ZIP under an existing version -
Jellyfin compares versions to decide whether to offer an update, so an already installed
server would keep the old file forever while the manifest advertises a different checksum
under the same number. Raise the last part instead, even for a build-metadata-only change.

Bump the third part for fixes within a line. No version number may appear twice in the
manifest: Jellyfin filters by `targetAbi` and then takes the highest version, so equal
numbers would be decided by array order alone - and a server moving from 10.11 to 12
would not be offered the matching build as an update.

## Install via the plugin catalogue (recommended)

Dashboard -> Plugins -> Repositories -> add:

```
https://raw.githubusercontent.com/MarcFauser/jellyfin-plugin-jflint/main/manifest.json
```

JFLint then appears under Catalogue. Jellyfin only offers the build whose `targetAbi`
your server satisfies, verifies the download against the MD5 in the manifest, and
handles later updates by itself.

## Install by hand

Jellyfin loads plugins from `<ProgramDataPath>/plugins/`. Ask the server where that is:

```powershell
(Invoke-RestMethod "$ServerUrl/System/Info" -Headers $h).ProgramDataPath
```

Then, on the server, extract the ZIP for your Jellyfin line into a folder underneath it
and restart Jellyfin:

```
<ProgramDataPath>/plugins/JFLint_1.0.0/
    Jellyfin.Plugin.JFLint.dll
    meta.json
```

Verify it came up:

```powershell
Invoke-RestMethod "$ServerUrl/Plugins" -Headers $h | Where-Object Name -eq 'JFLint'
(Invoke-RestMethod "$ServerUrl/api-docs/openapi.json" -Headers $h).paths.'/JFLint/EpisodesWithoutSeasonDB'
```

`meta.json` carries `targetAbi`; the plugin loads only when the server version is at
least that value. A mismatch shows up as status `NotSupported` rather than as a silent
failure.

## Development notes

- `jellyfin.ruleset` is taken verbatim from `jellyfin-plugin-template`, so
  `TreatWarningsAsErrors` can stay on without fighting Jellyfin's house style.
  Everything the ruleset does not exempt fails the build.
- Install the changelog guard once per clone: `./install-git-hooks.ps1`.

## License

[AGPL-3.0](LICENSE). Note that Jellyfin itself is GPL-2.0 and the other plugins in the
ecosystem are GPL-3.0; whether a plugin forms a combined work with its host is a legal
question this project does not attempt to settle.
