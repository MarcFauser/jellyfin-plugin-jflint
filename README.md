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

Both require an authenticated **administrator** (`RequiresElevation`) because the
response contains media file paths.

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
            $treffer = Invoke-RestMethod -Uri "$ServerUrl/$route" -Headers $Headers
            Write-Verbose "JFLint: $route"
            return , @($treffer)   # das Komma verhindert das Entrollen einer 1-Element-Liste
        }
        catch
        {
            $code = $_.Exception.Response.StatusCode.value__

            if ($code -ge 500)
            {
                Write-Warning "$route antwortete $code - das Plugin passt vermutlich nicht mehr zur Jellyfin-Version. Weiter mit dem naechsten Weg."
            }
            elseif ($code -ne 404)
            {
                throw   # 401/403 und alles Uebrige: NICHT ausweichen, sonst wird es still langsam
            }
        }
    }

    Write-Verbose 'JFLint nicht verfuegbar - alter Weg.'
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

Publish in this order:

```powershell
./build.ps1 -Changelog "what changed"
gh release create v11.1.0.0 dist\jellyfin-plugin-jflint_11.1.0.0.zip --title v11.1.0.0
git add manifest.json && git commit -m "Release 11.1.0.0" && git push
```

## Versions

The major version says which Jellyfin line a build belongs to:

| Plugin version | Jellyfin | targetAbi |
|---|---|---|
| `11.x.x.x` | 10.11.x | `10.11.0.0` |
| `12.x.x.x` | 12.x | `12.0.0.0` |

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
