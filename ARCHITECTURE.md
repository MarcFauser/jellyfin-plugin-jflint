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

### Measured, 2026-07-31

| | |
|---|---:|
| `ItemsByPathDB` | **6.8 ms** |
| `ItemsByPath` (twin) | 12.4 s |
| what it replaces: the caller reading every item and matching locally | ~7 s |

The database route is the fastest thing in the plugin, which is what the `Path` index buys.

**The twin is slower than the fallback it is supposed to precede**, and deliberately so:
it materialises *every* item type, ~50,000 rows, where the layout routes only touch TV. It
earns its place as the cross-check and as insurance against a schema change - it is not a
faster path to the same answer. A caller should go from `ItemsByPathDB` straight to its own
enumeration and use `ItemsByPath` only to verify, not to be quick.

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

## Duplicates: the first time the two lines actually diverged

`DuplicateEpisode` and `DuplicateMovie` look across folders, where every other check looks
at one folder against itself. The grouping key for episodes is
`SeriesPresentationUniqueKey`, a column on `BaseItemEntity` with no counterpart in
`BaseItemDto` - which is the whole reason the question cannot be asked over the stock API.

`SeriesId` is not a substitute, and this was measured rather than argued: Death Note is
held in two folders, so it is **two `Series` items with two `SeriesId`s** covering 37
episode numbers twice. Supplying a `userId` merges the *series* - 1,596 series become 520 -
but the episodes keep the `SeriesId` of the folder they live under, so a user-scoped query
is no way around it either.

**This pair is where the schema drift finally showed up.** `PrimaryVersionId` is a
`string` column in 10.11 and a `Guid?` in v12; the net10.0 build simply refused to compile
against the other line's shape. Every route here has carried the warning that
`JellyfinDbContext` is not a promised plugin contract - this is the first case that proved
it. A payload that changes shape with the server line would push the problem onto every
caller, so both are reported as a `Guid?`: **the newer of the two shapes, not the older**.
The 10.11 string holds an item id and parses; where it would not, the link is reported as
absent rather than handed on in a form the caller cannot use.

That direction is the point. Normalising onto the shape being replaced would have to be
undone the day 10.11 support ends, and every caller written against it with it.

A quieter divergence in the same routes: `Width` and `Height` are `int?` on the entity and
plain `int` on the object model. Left alone, the two routes would report `null` and `0` for
the same unknown value and the pair would disagree on rows that are in fact identical. Both
sides normalise zero to null.

**Double episodes are a known blind spot.** `IndexNumberEnd` is not a column - it survives
in the `Data` blob, which `ILibraryManager` deserialises and a column query cannot see. A
file covering E01-E02 carries `IndexNumber = 1` alone and never collides with a separate
E02. The object-model route could see it and deliberately does not: a pair that disagrees
is worse than a pair with a blind spot that is written down.

### Measured, 2026-08-02

| route | `…DB` | `ILibraryManager` | rows | groups |
|---|---:|---:|---:|---:|
| `DuplicateEpisode` | 142 ms | 2.87 s | 1,598 | 701 |
| `DuplicateMovie` | 108 ms | 656 ms | 273 | 124 |

Both pairs agree item by item. **Death Note comes back as 74 rows across 37 numbers under
a single `SeriesKey`** - where `SeriesId` gives two. That one line is the entire argument
for the route.

Two independent estimates existed beforehand, both built on a `SeriesName` proxy, and the
measurement differs from each in a direction that explains itself:

- **Episodes came in lower** (1,598 / 701 against ~1,700 / ~750) because the proxy
  over-grouped series that merely share a title. *Infiltration* is the case that was
  checked by hand: two shows of that name, reported by the proxy, **absent here**.
- **Movies came in higher** (273 / 124 against 250 / 119) because the estimate counted TMDB
  ids only. Grouped by source: `Tmdb:` 250 rows in 119 groups - matching it exactly - plus
  `Imdb:` 23 rows in 5 groups that a TMDB-only count cannot see.
- **`Key:` produced 0 groups**, as a review predicted from the source: an unlinked movie's
  presentation key is its own item id.

**One property could not be exercised.** `PrimaryVersionId` came back null on all 1,871
rows - but the control is null too: no movie and no episode in this library has more than
one `MediaSource`, so nothing is linked as an alternate version anywhere. The field is
therefore correct-by-vacuity, and the string-to-`Guid` parse on 10.11 remains **unverified**
rather than proven. It is on the v12 checklist for that reason.

## The one destructive route

`DeleteItemKeepFile` is the only route here that changes anything, and the only one
without a twin - there is nothing to cross-check, and a second way to delete would be a
second way to be wrong.

Its safety is **structural, not conditional**, which is the only design decision worth
recording. Three choices, each of which removes a way to be wrong rather than checking for
it:

| instead of | this |
|---|---|
| a `deleteFile=false` parameter | no parameter at all - the route cannot delete a file |
| trusting the caller to delete children first | `409` when a folder still has descendants |
| a `{itemId:guid}` route constraint | parse the id, `400` when it is malformed |

The third is the least obvious and the most dangerous to get wrong. A `:guid` constraint
makes a malformed id fail to *match the route*, and ASP.NET answers an unmatched route with
`404` - which is precisely the signal the caller reads as "the plugin is not installed",
sending it back to the stock route that does delete files. A bad request would have
silently escalated into the operation this route exists to avoid.

**Both `DeleteOptions` flags are written out**, including `DeleteFileLocation`, which the
class already defaults to `false`. That class belongs to `MediaBrowser.Controller`, so its
defaults are another project's implementation detail, not a promise to this one - and its
constructor already sets the *other* field, which shows the file is a place where defaults
get decided. The rule generalises: **where a default you rely on is owned by someone else,
state it.** One assignment against the user's media library is not a close call.

`DeleteFromExternalProvider = false` departs from the stock route deliberately. A stale
entry is a bookkeeping fault, not a deletion; reporting it outward would push this side's
error into a service that was right.

### What is argued rather than measured

**That the file survives is verified at the source, not on a server.** With
`DeleteFileLocation = false` the branch containing every `File.Delete` is never entered -
the guard is `(options.DeleteFileLocation && item.IsFileProtocol) || IsInternalItem(item)`,
and `IsInternalItem` matches only Genre, MusicArtist, MusicGenre, Person, Studio and Year.
Demonstrating it would need a throwaway file in a watched library and a control run of the
stock route on a second copy; every library root on the reference server is a read-only
mount, so neither half is possible there. Stated here rather than left to look tested.

The same read-only mounts make one upstream behaviour worth knowing, because it is the
opposite of what one would guess. `DeleteItemPath` rethrows `IOException` and
`UnauthorizedAccessException` for the first path, which is always the required one, and the
file deletion runs *before* the repository removal:

| the item's file is | `DELETE /Items/{itemId}` on a read-only mount |
|---|---|
| already gone | nothing to delete, the entry is removed - works |
| still there | throws, and **the entry stays** |

So on such a mount the stock route cannot remove an entry whose file came back between the
scan and the click - exactly the case this route exists for. `DeleteItemKeepFile` turns
that failure into the intended outcome rather than merely being safer.

**The permission check is thinner than it looks.** `item.CanDelete(user)` is kept, mirroring
the stock controller, but the user comes from `IAuthorizationContext` - and an API-key
caller has no user, so the check is skipped, exactly as upstream. What actually guards this
route is `Policies.RequiresElevation` on the controller, which is stricter than the stock
delete's plain `[Authorize]`.

`Jellyfin.Api` is not on NuGet, so the upstream `User.GetUserId()` extension is unavailable
to a plugin. `IAuthorizationContext` in `MediaBrowser.Controller.Net` provides the same two
values - `User` and `IsApiKey` - from a package that is.

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

## The response shape is invariant, on purpose

Jellyfin serializes every response with `DefaultIgnoreCondition = WhenWritingNull`
(`src/Jellyfin.Extensions/Json/JsonDefaults.cs`). A null property is therefore **not**
sent as `null` - it is missing from the JSON. Every nullable member of every DTO here
carries `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` to opt out of that, so a
field that is declared is always present.

**Why it is worth deviating from the server's convention.** These routes have exactly one
consumer, and the failure mode is silent on both sides: drop or rename a field and nothing
fails to compile, the caller simply starts throwing on a property that is not there. It
happened twice - `GroupSize`, then `PrimaryVersionId` - and the second time it cost a full
handover round trip to diagnose, because *"the plugin does not send it"* and *"every value
is null"* look identical from the outside.

The shape was only ever *accidentally* stable. Measured 2026-08-02: `ProductionYear` was
absent on 2 of 273 movie rows while every other field happened to be filled. And
`LayoutFindingDto` is worse by design - one shape serves five finding kinds, so most of
its fields are null for any given row.

**Two things this deliberately is not.** It is not applied to `PrimaryVersionId` alone;
that would leave the same trap on eight other fields and make one payload inconsistent
with itself. And it is not done by registering `IConfigureOptions<JsonOptions>`, which
would change the serialization of **every** response the server sends.

What stays checkable without a consumer: `components.schemas.<Dto>.properties` in
`GET /api-docs/openapi.json` is generated from the DTO and cannot drift from what the
route sends. That is the only way to guard a field which has no value anywhere - the
acceptance suite asserts the declared field list of both duplicate DTOs for exactly that
reason.

## What is deliberately not here

- **No jf-lint changes.** Rewriting `$scanEpisodesScript` belongs in that repository and
  must keep the current code path as a fallback for servers without this plugin.
- **No pull request against Jellyfin.** The general fix is a `hasParentIndexNumber`
  filter on `/Items`, for which `HasOfficialRating` is an exact template - three files,
  ~20 lines. It would land in 12.1 at the earliest, so it solves nothing today. The
  sketch is in the handover.
- **No configuration page.** There is nothing to configure.
