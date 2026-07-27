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
