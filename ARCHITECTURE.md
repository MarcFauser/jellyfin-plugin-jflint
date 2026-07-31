# Architecture

How the plugin is put together and why. For the motivation see [HISTORY.md](HISTORY.md).

## Layout

```
Jellyfin.Plugin.JFLint.slnx
Jellyfin.Plugin.JFLint\
    Jellyfin.Plugin.JFLint.csproj    net9.0 + net10.0, TreatWarningsAsErrors
    Plugin.cs                        BasePlugin<PluginConfiguration>, fixed GUID
    Configuration\PluginConfiguration.cs   empty on purpose - nothing to configure
    Controllers\JFLintController.cs        the episode question, both routes
    Controllers\LibraryLayoutController.cs five layout findings, both routes each
    Controllers\ItemLookupController.cs    ItemsByPath - a lookup, not a finding
    Models\OrphanEpisodeDto.cs             record, six fields
    Models\LayoutFindingDto.cs             one shape for all ten layout routes
    Models\LayoutFindingKind.cs            the five kind names, route and payload alike
    Models\PathItemDto.cs                  record, four fields
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

## Library layout findings

Five more questions of the same nature, each again on two routes. They share one DTO so a
caller needs one parser, and the five `ILibraryManager` routes share one internal pass, so
a single request materialises the library once no matter which kind it asks about.

Two decisions in there are not obvious, and both were the difference between a plausible
answer and a correct one.

**An unset link is `Guid.Empty`, not `NULL`.** `BaseItemRepository.Map()` normalises
`ParentId` (`!dto.ParentId.IsEmpty() ? dto.ParentId : null`) but assigns `SeriesId` and
`SeasonId` straight from non-nullable `Guid` sources, whose "nothing found" value is
`Guid.Empty`. An anti-join written against `NULL` alone therefore reports every seasonless
episode as orphaned - 6 instead of 1 on the reference library, five of them duplicates of
what `EpisodesWithoutSeason` already reports - and groups every series-less season under
one key.

**"Beneath" is `ParentId`, not `SeasonId`.** `Episode.FindSeasonId()` falls back to
matching `ParentIndexNumber` against the series' children when the file is not in a season
folder, so `SeasonId` can name a season the file does not physically sit under. That is
precisely the shape `PhantomSeason` exists to find, so the physical link is the right one.
Both give identical numbers on the reference library and disagree on none of its 9,895
seasons; the choice is about the libraries where they would differ.

### Measured, 2026-07-30

Against the live server (Jellyfin 10.11.11, 1,585 series / 9,895 seasons / 30,088 episode
rows). Best of several runs after a warm-up call.

| Finding | `…DB` | `ILibraryManager` | Hits |
|---|---:|---:|---:|
| `PhantomSeason` | 17.6 ms | 6.05 s | 332 |
| `SeasonFolderWithoutVideo` | 13.8 ms | 5.99 s | 4 |
| `DuplicateSeasonNumber` | 23.7 ms | 5.99 s | 6 |
| `SeriesWithoutFiles` | 45 ms | 5.99 s | 7 |
| `OrphanedItem` | 78.1 ms | 5.91 s | 1 |

Every pair returns the **identical set of item ids**, compared item by item. `~22 s` was
the cost of answering the same three tabs over the stock HTTP API.

The object-model route costs about six seconds whichever finding is asked for - it
materialises the library once per request, and all five kinds fall out of that one pass.
Five separate requests therefore cost five times that, which is the price of failing over
per finding rather than per bundle.

**Comparing ids alone is not enough.** `EpisodeRowCount` is computed by both routes, so an
error in it would have agreed with itself; it was compared separately, field by field.

#### `SeriesWithoutFilesDB` was 15.8 s until the index was checked

The obvious form of the query - `NOT EXISTS (... WHERE SeriesId = series.Id)` - took
**15.8 s**, slower than the fallback it exists to replace, while its four siblings answered
in 13-78 ms. `BaseItems` carries a dedicated index on `ParentId` and **none on `SeriesId`**,
read off the EF model (`db.Model.FindEntityType(typeof(BaseItemEntity)).GetIndexes()`), so
the correlated subquery was one table scan per series row, 1,585 of them.

One grouped pass over the episode rows - `Count()` for the total, `Sum(e => e.IsVirtualItem
? 0 : 1)` for the playable ones - answers both questions in a single scan and brought it to
**45 ms**, a factor of about 350.

Two things deliberately not done: no index was added, because that is Jellyfin's schema
and a plugin that migrates its host's database promises far more than this feature is
worth; and the finding was not switched to the indexed `ParentId`, because "anywhere
beneath this series" is the actual question and `Episode.ParentId` points at a season.

## The one lookup: `ItemsByPath`

Every other route is a whole-library finding with no parameter. This one takes a path,
covers **every** item type rather than just TV, and answers "what is *here*" instead of
"what is broken". The departure is deliberate and is recorded here rather than left to be
noticed: a caller that holds a path and needs an item id has no other way, because `/Items`
exposes `Path` as an output field only. The alternative it replaces is reading ~50,000 rows
in ~7 s to identify one item.

The pair convention is kept, but the twin is weaker than elsewhere.
`InternalItemsQuery.Path` exists and would serve the exact half - it is an equality filter
(`e.Path == GetPathToSave(filter.Path)`) - but nothing in the query object expresses
"beneath". The `ILibraryManager` route therefore materialises the library and filters in
memory. That is the schema-change insurance, not a route to reach for.

### `StartsWith` would have thrown away the index

`BaseItems` carries an index on `Path`, which is the whole reason this route can be fast.
It is only reached by a query the planner can use as a range, and the obvious form is not
one. Read off the generated SQL before shipping:

| written as | SQL | uses the index |
|---|---|---|
| `Path.StartsWith(prefix)` | `Path LIKE @p ESCAPE '\'` | **no** - SQLite skips the LIKE optimisation when an ESCAPE clause is present |
| `Path >= p + "/"` and `< p + "0"` | plain range | yes |

`'0'` is `'/' + 1`, so the half-open range is exactly "starts with `prefix/`". Anchoring on
the separator is also what stops `/Movies/Ring` from matching `/Movies/Ring2` - the single
most likely way to get this route quietly wrong.

Two smaller findings from the same probe, both measured rather than assumed:

- **The ordinal-explicit forms do not translate at all.** Both
  `string.Compare(a, b, StringComparison.Ordinal)` and `string.CompareOrdinal` throw at
  query compilation; `CompareTo` translates. It is culture-sensitive in C# and irrelevant
  here, because EF turns it into a SQL comparison under the column's BINARY collation and
  refuses to evaluate on the client.
- **The stored path is the path the API reports.** `InternalItemsQuery.Path` runs its input
  through `ReverseVirtualPath`, so a server using path substitutions could store something
  else. Checked on this one by comparing the `Path` of both existing episode routes - the
  database column against the object model - ordinally, across every row: no difference.

### Checking the SQL without a server

EF Core reports an untranslatable query at *query-compilation* time, which on a server
means at runtime, in front of the user. All six database queries are therefore compiled
offline against a throwaway SQLite `JellyfinDbContext` and their `ToQueryString()` read -
never executed, no data needed. That confirmed the correlated `EXISTS` in each finding, the
`GROUP BY … HAVING COUNT(*) > 1` behind the duplicate check, and that `Guid.Empty` arrives
as a parameter rather than tripping the translator. The probe lives in `tmp\` and is not
versioned; it is a check, not a test suite.

What it cannot tell us is whether the numbers are right. That needs the live server, and
the request document carries the expected counts for exactly that comparison.

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
