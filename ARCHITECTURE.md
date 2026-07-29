# Architecture

How the plugin is put together and why. For the motivation see [HISTORY.md](HISTORY.md);
for the underlying research and its citations see
[HANDOVER-episodes-endpoint.md](HANDOVER-episodes-endpoint.md).

## Layout

```
Jellyfin.Plugin.JFLint.slnx
Jellyfin.Plugin.JFLint\
    Jellyfin.Plugin.JFLint.csproj    net9.0 + net10.0, TreatWarningsAsErrors
    Plugin.cs                        BasePlugin<PluginConfiguration>, fixed GUID
    Configuration\PluginConfiguration.cs   empty on purpose - nothing to configure
    Controllers\JFLintController.cs  both endpoints
    Models\OrphanEpisodeDto.cs       record, six fields
jellyfin.ruleset                     verbatim from jellyfin-plugin-template
build.ps1                            publish -> meta.json -> dist\*.zip
```

`Plugin.cs` contributes no pages and no scheduled tasks. It exists because Jellyfin
discovers plugin assemblies through the plugin class; the actual work is the controller,
which the server picks up because `AddJellyfinApi` registers plugin assemblies as MVC
application parts.

## Why two endpoints for one question

| | `EpisodesWithoutSeason` | `EpisodesWithoutSeasonDB` |
|---|---|---|
| Route | `ILibraryManager.GetItemList` | `JellyfinDbContext.BaseItems` |
| Filtering | in memory, after every episode is materialised | in SQL, only matches leave the database |
| Interface | public and promised | not a plugin contract; schema may change per major version |
| Fails | practically never | loudly, at query translation - not silently |

The database route is expected to be the fast one, but the point of keeping both is not
speed: **they check each other.** Identical inputs must produce identical sets. A
divergence means the hand-written SQL filter is wrong, and it shows up on the first
comparison rather than months later in a stale report.

### Measured, 2026-07-29

Against the live server (Jellyfin 10.11.11, 30,077 episodes, 6 of them without a
season). Three runs per endpoint, best value shown.

| Route | Time | Hits |
|---|---:|---:|
| Fetch every episode and filter client-side | 25.4 s | 6 |
| `EpisodesWithoutSeason` (`ILibraryManager`) | 2.9 s | 6 |
| `EpisodesWithoutSeasonDB` (`JellyfinDbContext`) | **28 ms** | 6 |

**All three return the same six item ids** (compared with `Compare-Object`, not just by
count). The 25.4 s is a single request; the calling tool pages in six rounds and takes
about 47 s for the same result, so the database route is roughly 1700× faster there and
the `ILibraryManager` route about 16×.

Worth knowing what the check actually finds: four of the six are not episodes at all but
films and compilations filed as one - `Psych The Movie`, both parts of
`Farscape The Peacekeeper Wars`, a retrospective and a tribute. The plugin reports the
defect; deciding what to do with each is the calling tool's job.

Two details that keep the database route honest:

- The episode type string is **not** hardcoded. `BaseItemEntity.Type` holds a fully
  qualified type name, and `IItemTypeLookup.BaseItemKindNames[BaseItemKind.Episode]`
  is the same lookup Jellyfin uses internally.
- `IsVirtualItem` has to be excluded explicitly. `ILibraryManager` applies Jellyfin's
  own visibility logic; raw table access does not.

## Multi-targeting

Jellyfin 10.11 runs on .NET 9, Jellyfin 12 on .NET 10. Because the target frameworks
differ, plain `<TargetFrameworks>net9.0;net10.0</TargetFrameworks>` plus one conditional
`ItemGroup` per line is enough - no custom MSBuild properties.

`net9.0` is a constraint, not a preference: a `net10.0` assembly cannot load into the
.NET 9 runtime Jellyfin 10.11 runs on.

Everything the plugin touches is identical in both branches, verified by diffing the
sources of `release-10.11.z` against `master`: `ILibraryManager.GetItemList`, the
`InternalItemsQuery` fields used, `IItemTypeLookup`, and `Policies`. The v12 artifact
therefore compiles - but it is **untested**, because no v12 server exists here.

## Reproducible artifacts

The point: rebuilding without a source change must not invalidate the checksum of an
already published release. Four sources of drift, all found by measurement, three of them
mine:

| Source | Fix |
|---|---|
| Timestamp written into `meta.json` | pinned to the last commit touching `Jellyfin.Plugin.JFLint/` |
| Per-file times stored by `Compress-Archive` | set to that same instant before zipping |
| SDK appends the HEAD commit to `AssemblyInformationalVersion` | `IncludeSourceRevisionInInformationalVersion=false` |
| SourceLink writes the HEAD commit into the PDB, whose checksum the DLL carries | `EnableSourceControlManagerQueries=false` |
| Absolute build path in the assembly's debug directory | `PathMap` rewrites it to `/_/` |

The last two are the interesting ones: **every** commit changed the DLL, including commits
that only touched the README. The second was invisible from the outside - the commit hash
never appeared in the DLL, only in the PDB, and the assembly's debug directory holds that
PDB's checksum.

**How this was almost missed.** The first check built twice in a row and compared hashes.
They matched - because nothing had changed, so MSBuild skipped compilation entirely and
never touched the file. A valid check needs a commit in between and a forced rebuild
(`dotnet clean`). The second attempt at that check was also worthless: the commit was
rejected by the changelog hook, so both builds ran on the same HEAD. Verified on the third
attempt, which asserts that HEAD actually moved before comparing.

**Path independence.** `ContinuousIntegrationBuild=true` is the usual switch for this and
does nothing here - measured: identical hash, the `obj\` path still sat in the debug
directory. It normalises paths via `SourceRoot`, which comes from the SCM queries that had
to be switched off. `PathMap` achieves it without them by rewriting the project directory
to `/_/`. Verified the only way that means anything: the same sources built from two
differently named directories produce a byte-identical assembly, with zero absolute paths
left in it.

## Package references

| Package | Why |
|---|---|
| `Jellyfin.Controller` | `ILibraryManager`, `InternalItemsQuery`, `Episode`, `IItemTypeLookup` |
| `Jellyfin.Model` | `BaseItemKind` and friends |
| `Jellyfin.Database.Implementations` | `JellyfinDbContext`, `BaseItemEntity` |
| `Microsoft.AspNetCore.App` (FrameworkReference) | `ControllerBase`, `[ApiController]`, `[Authorize]` |

All Jellyfin packages use `ExcludeAssets="runtime"`: the server ships these assemblies,
and a copy next to the plugin DLL would shadow them. `build.ps1` enforces this a second
time by deleting everything from the publish output that is not ours - verified by
listing the ZIP contents.

`Jellyfin.Api` is **not** on NuGet. The authorization policy constants therefore come
from `MediaBrowser.Common.Api.Policies` (package `Jellyfin.Common`), which is where they
actually live in both branches.

## Authorization

`[Authorize(Policy = Policies.RequiresElevation)]` on the controller: the responses
contain absolute media paths, which is administrator-grade information.

## What is deliberately not here

- **No jf-lint changes.** Rewriting `$scanEpisodesScript` belongs in that repository and
  must keep the current code path as a fallback for servers without this plugin.
- **No pull request against Jellyfin.** The general fix is a `hasParentIndexNumber`
  filter on `/Items`, for which `HasOfficialRating` is an exact template - three files,
  ~20 lines. It would land in 12.1 at the earliest, so it solves nothing today. The
  sketch is in the handover.
- **No configuration page.** There is nothing to configure.
