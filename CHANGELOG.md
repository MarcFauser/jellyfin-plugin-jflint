# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `build.ps1 -Publish`: creates one GitHub release per artifact and pushes the updated
  `manifest.json`, in that order. A manifest entry whose release does not exist yet is a
  failed download in the dashboard, so the releases go first, each uploaded ZIP is fetched
  back and its MD5 compared against the manifest - what Jellyfin itself does before
  installing - and the manifest follows only after that. Refuses up front on an empty
  changelog, missing or unauthenticated `gh`, uncommitted plugin source, or a version
  whose release already exists. The checks sit before the build on purpose: a refused run
  had otherwise already rewritten `manifest.json`, replacing the changelog of an
  already published version in the working copy.
- `logo.webp` as the catalogue tile, referenced from `manifest.json` via `imageUrl`.
  240x240 with a real alpha channel (`VP8X` + `ALPH`), 15.8 KB - it sits on Jellyfin's
  dark dashboard without a background box. Deliberately no new plugin version: the logo
  lives in the manifest, not in the plugin ZIP, so both artifacts stayed byte-identical
  and `11.1.0.1` / `12.1.0.1` remain valid.

## [11.10.0.0] / [12.10.0.0] - 2026-08-07

### Added
- `FileNameTitle` / `FileNameTitleDB`: entries whose title is nothing but the file or folder
  they came from - `Trio.mit.vier.Faeusten.S02E01.Unheilvoller.Besuch`, or a season named
  after an episode's release folder. Not cosmetic: it is what anyone opening the library
  sees, and a season named that way means Jellyfin made one season per episode folder.
- A **sixth `LayoutFindingKind`** rather than a seventh DTO, so a caller keeps one parser.
  `LayoutFindingDto` gains `Reasons` and `HasProviderIds`, both carrying
  `JsonIgnoreCondition.Never` like every other nullable member.
- `Reasons` is a **list**, not a single value: the rule's two halves are joined by OR and a
  third of the findings satisfy both - 278 of 810 on the reference library, including every
  one of its season findings. One string could not have described them.
- `HasProviderIds` is `bool?`, not `bool`. A plain bool would serialise as `false` on the
  five kinds that never compute it, which reads as an answer rather than as "not asked".
- The first kind that also covers `Movie`, so the object-model route now materialises movies
  as well.

- `PerEpisodeFolder` / `PerEpisodeFolderDB`: a season that is really one episode's own
  folder, from a release that gives every episode a directory. Jellyfin resolves each as a
  season, so a series shows twenty seasons of one episode and the season names are release
  strings. A layout fault rather than a metadata one - only flattening the folders on disk
  repairs it, which is why it wants a list of its own.
- It borrows `FileNameTitleRule.LooksLikeAFileName` rather than restating the condition. Two
  rules that look alike are two rules that drift apart.
- The episode count is deliberately **not** part of it. Measured upstream: the ten seasons
  with a path that hold more than one episode are exactly the ten the name condition already
  drops. Two conditions agreeing without being the same condition is worth more as a control
  than as a second clause.
- **Its rows are also `FileNameTitle` findings**, necessarily - a per-episode folder produces
  a season whose title is a file name. Not double counting: the two answer different
  questions and want different repairs. Documented on the kind so nobody reports it as a
  defect.

### Changed
- `Sorted` ends on `ItemId`. Without a unique tiebreaker the order of rows agreeing on
  series, season and name is whatever each half happens to produce, and the `X`/`XDB` pair
  stops being comparable element by element - the only reason the pair exists. Measured
  reachable: the same episode held in two releases ties on every other key. This also
  reorders ties in the five existing kinds, which were arbitrary before.

### Notes on the implementation, because two of them are traps
- The rule is a **verbatim port** of the calling tool's `FileTitleScan.cs`, kept in
  `FileNameTitleRule` and shared by both halves. A better rule that differs would be worse:
  the two route halves and that tool's own fallback are each other's controls only while all
  three reach the same verdict. A reconstruction was tried and measurably failed - a
  trailing-group test written without an anchor matches the hyphen inside
  `eps1.1_ones-and-zer0es.mpeg` and keeps in precisely the rows the clause exists to let out.
- The first predicate uses `EF.Functions.Like(e.Name!, "%.%")`. `Contains(".")` is a **CA1847
  build error** under this project's `AnalysisMode=AllEnabledByDefault`, and the fix the
  analyzer itself suggests - `Contains('.')` - compiles clean, translates on EF Core 10 and
  **throws at query time on the whole EF Core 9 line**, which is what Jellyfin 10.11 ships.
  It would have shipped green and answered 500 on the server it was built for. Verified
  before release by translating the route's actual query on EF Core 9 (965 chars of SQL),
  with `Contains('.')` in the same run as the negative control.
- Both path separators are tested. On a Windows server the forward-slash clauses never match,
  and testing only `/` would silently drop the entire second half of the rule there.

## [11.9.0.0] / [12.9.0.0] - 2026-08-06

### Fixed
- **`11.8.0.0` cleared the wrong instance.** It nulled the physical library folders returned
  by `CollectionFolder.GetPhysicalFolders(true)`, which resolves them through `GetItemById`.
  A user-less query does not walk those. `ItemsController` calls
  `GetParentItem(null, null)`, which returns `LibraryManager.RootFolder` - the
  `AggregateFolder` - and `AggregateFolder.LoadChildren()` uses `base.LoadChildren()` on its
  first call, so its `_children` holds **repository-built** physical folder objects that were
  never registered anywhere. The divergence is not below the physical library folder; it
  **is** the physical library folder.
- Both are cleared now. `DeleteItemKeepFile` and `ForgetCachedChildren` additionally null
  `RootFolder.Children`, which is also what makes the two views converge: the aggregate root
  has recorded its child ids by then, so its next load resolves them through `GetItemById` -
  the same objects the collection folders use.

### How the mistake was caught, since it is the useful part
- The measurement that prompted `11.8.0.0` was sound but was read one level too low. What
  settled it afterwards was a check the fix itself invited: **all three parents turned out to
  be physical library folders**, which meant Jellyfin's own `DeleteItem` had already nulled
  exactly what `11.8.0.0` set out to null. A fix that does what the broken code already does
  cannot work, and that contradiction is what sent the reading back to the source.
- A wrong turn on the way, recorded because it looked convincing: the non-recursive
  `ParentId=` probe was briefly taken for a SQL query, which would have made the whole
  experiment meaningless. `Folder.GetItemsInternal` line 1000 reads
  `items = Children.Where(filter)` when there is no user - it is served from memory as well,
  so the comparison held.

## [11.8.0.0] / [12.8.0.0] - 2026-08-06

### Fixed
- **A removed entry kept being answered from memory until the server was restarted.**
  `DeleteItemKeepFile` now also drops the cached children of the physical library folder the
  item sat under. Measured on 10.11.11 over three stranded entries: present in
  `GET /Items?Recursive=true&IncludeItemTypes=Series` **without** a `userId`, absent with
  one, and absent from the database. A caller could not tell a removal that worked from one
  that failed, and re-listed the same rows on every scan.
- The cause is not the obvious one, so it is written down where the code is. Jellyfin's own
  `DeleteItem` ends with `if (parent is Folder folder) { folder.Children = null; }` and that
  runs - all three parents resolved. It has no effect because the parent comes from
  `GetItemById` (LRU or freshly retrieved) while a folder's own children come from
  `Folder.LoadChildren()` → `GetCachedChildren()` → `ItemRepository.GetItemList(...)`, which
  bypasses the library manager and registers nothing. **Two instances per id**: Jellyfin
  nulls one, `AddChildrenToList` walks the other along object references. Measured rather
  than reasoned - the three entries were absent from `GET /Items?ParentId=<their parent>`,
  which resolves that parent by id, while still present in the walk from the root.
- The lever is `CollectionFolder.GetPhysicalFolders`, which resolves through `GetItemById`:
  for a physical library folder the instance reachable by id **is** the one the root walk
  descends into, so one assignment detaches the whole stale subtree.

### Added
- `POST /JFLint/ForgetCachedChildren`: drops the cached children of every physical library
  folder, for entries stranded before the fix above or removed by a route that does not know
  about it. Returns the folders it cleared rather than a count.
- No path or id parameter, deliberately: the entries this clears are precisely the ones the
  database no longer holds, so neither resolves to anything. There is nothing to aim with,
  and narrowing it would mean deriving an ancestor from a path as a string.
- **Not** a route pair. `X`/`XDB` exists so each half is the other's control, which suits a
  question with a comparable answer; running a mutation twice would double it, not check it.

## [11.7.0.0] / [12.7.0.0] - 2026-08-02

### Changed
- **Every nullable field is now always present in the payload.** Jellyfin serializes with
  `DefaultIgnoreCondition = WhenWritingNull`, so a null field was not sent as `null` - it
  was absent from the JSON entirely. All 35 nullable members across the six DTOs now carry
  `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`, which overrides that per property
  (measured against Jellyfin's own options before shipping, with a control run without the
  global condition). The response shape is therefore the same for every row of every route.
- Why this is worth a behaviour change: the shape was only *accidentally* stable. On this
  library `ProductionYear` was already absent on 2 of 273 movie rows while every other
  field happened to be filled, so a consumer reading a field directly worked until it hit
  one of those two. `LayoutFindingDto` is the sharper case - four of its five finding kinds
  leave most fields null **by design**, and `GroupSize` is exactly the field that took the
  companion tool down. A caller under `Set-StrictMode` throws on a property that is not
  there, and nothing on either side fails to compile when it disappears.
- Deliberately applied to all six DTOs rather than to `PrimaryVersionId` alone. Fixing the
  one field that prompted this would have left the same trap on eight others and made one
  payload inconsistent with itself.
- **Not** done by registering `IConfigureOptions<JsonOptions>`: that would change the
  serialization of every response the server sends, not just this plugin's.

## [11.6.0.1] / [12.6.0.1] - 2026-08-02

### Fixed
- The two duplicate routes filtered **empty strings** differently: the object-model side
  used `string.IsNullOrEmpty`, the database side only `!= null`. An empty
  `SeriesPresentationUniqueKey` would therefore be dropped by one twin and kept by the
  other, where it would group every such episode into one bucket and report the lot as
  duplicates of each other - the precise failure the pair exists to make impossible. This
  is the same shape already recorded for `DuplicateSeasonNumber` and `Guid.Empty`; the
  lesson was in the project and did not reach this file.
- Reported by review at one site; **three** existed. `Path` had the same split in both the
  episode and the movie query. All now read `!string.IsNullOrEmpty(...)` on both sides, in
  the same words, so a reader comparing the twins sees one predicate rather than two that
  happen to agree.

### Changed
- Documented what the `Key:` branch of the movie identity actually does.
  `Video.CreatePresentationUniqueKey()` returns `PrimaryVersionId` when one is set and the
  item's **own id** otherwise, so for an unlinked movie the key is unique by construction
  and cannot collide. The branch groups exactly one population - files already linked as
  alternate versions - and is **not** a fallback for the 118 movies here that carry no
  provider id at all. Those are simply not reported. The branch is kept because that one
  population is real; only the description was wrong.

## [11.6.0.0] / [12.6.0.0] - 2026-08-02

### Added
- `DuplicateEpisode` / `DuplicateEpisodeDB`: every episode file whose (series, season,
  episode) is covered by more than one real file. Grouped on
  `SeriesPresentationUniqueKey`, which is a column on `BaseItemEntity` and absent from
  `BaseItemDto` - the reason this cannot be asked over the stock API at all. Grouping on
  `SeriesId` instead loses precisely the case that matters: each folder of a series is its
  own `Series` item, and an episode keeps its folder's `SeriesId` even in a merged,
  user-scoped view.
- `DuplicateMovie` / `DuplicateMovieDB`: movie files sharing an identity - TMDB id, else
  IMDB id, else `PresentationUniqueKey`. No name-and-year fallback: the library holds
  genuinely distinct films with the same title, and a false positive costs more than a
  miss. Duplicates here are often wanted (1080p beside 2160p, cut beside uncut), so the
  payload carries size, resolution and the version link and leaves the judgement to the
  caller.
- Both pairs return **one row per file**, not per group, so the row count is roughly twice
  the number of affected slots. Virtual items never participate: they carry no file and
  are the other half of the same defect rather than a copy of anything.

### Fixed
- `PrimaryVersionId` is reported as a **`Guid?` on both Jellyfin lines**. It is a `string`
  column in 10.11 and a `Guid?` in v12 - found because the net10.0 build refused to
  compile against the 10.11 shape - while the object model holds a string on both. This is
  the first measured instance of the schema drift the dual-route design exists to survive.
  The payload follows where the field is going rather than where it has been, so the 10.11
  string is parsed; a value that will not parse is reported as absent rather than passed on
  in a shape a caller cannot use.
- `Width` and `Height` are normalised so that zero reads as null. The column is nullable
  and the object model is not, so without it the two routes would report `null` and `0`
  for the same unknown value and the pair would disagree on identical rows.

### Known limitation
- Double episodes are invisible to both routes. `IndexNumberEnd` is not a column on
  `BaseItemEntity` - it lives in the `Data` blob, which `ILibraryManager` deserialises and
  a column query cannot see. A file covering E01-E02 therefore carries `IndexNumber = 1`
  alone and never collides with a separate E02. The object-model route *could* see it; it
  deliberately does not, because a pair that disagrees is worse than a pair with a written
  down blind spot.

## [11.5.0.0] / [12.5.0.0] - 2026-07-31

### Changed
- The `409` from `DeleteItemKeepFile` now names the descendants instead of only counting
  them: a `DeleteConflictDto` with the exact `Remaining` count and a sample of up to
  twenty `BlockingChildDto` - id, type, name and **path**. Same status, same refusal, same
  guarantee.
- The path is the field that earns its place. A blocker without one is a virtual entry; a
  blocker with one is something the caller can go and look at on disk. A bare count left a
  caller that could find no children over HTTP with nowhere to go at all.

## [11.4.0.0] / [12.4.0.0] - 2026-07-31

### Added
- `DELETE /JFLint/DeleteItemKeepFile/{itemId}`: removes one library entry and leaves the
  media file where it is. The stock `DELETE /Items/{itemId}` hardcodes
  `new DeleteOptions { DeleteFileLocation = true }` and the flag is not reachable over
  HTTP, so a tool clearing a stale entry - one whose file is already gone - had no route
  that could not also delete media. Three decisions make the safety structural rather
  than conditional:
  - **No parameter.** There is nothing to forget or mis-set; the route cannot delete a
    file at all. A flag with one legal value is a flag somebody eventually makes
    configurable.
  - **`409` for a folder that still has descendants.** `LibraryManager.DeleteItem` hands
    the item and every recursive descendant to the repository as one batch, and that
    batch is what trips the `UserData` UNIQUE constraint of jellyfin#16120 - fixed in v12,
    not in 10.11.x. Deleting children first was a caller convention; refusing here makes
    it a guarantee.
  - **The id is parsed, not route-constrained.** A `:guid` constraint would make a
    malformed id miss the route, and ASP.NET answers a missing route with `404` - the
    very signal a caller reads as "plugin absent", sending it back to the stock route
    that deletes files. `400` for a bad id keeps `404` meaning one thing.
- Both `DeleteOptions` flags are written out, including `DeleteFileLocation`, which the
  class already defaults to `false`. That class belongs to `MediaBrowser.Controller`; its
  defaults are that project's implementation detail, and its constructor already sets the
  other field, so the file is demonstrably a place where defaults get decided. A release
  lining it up with the controller that always passes `true` would turn this route into
  the thing it exists to avoid, and nothing here would fail to compile.
- `DeleteFromExternalProvider = false`, departing from the stock route: a stale entry is a
  bookkeeping fault, not a deletion, and telling an external service otherwise would push
  this side's error outward into one that was right.

## [11.3.0.1] / [12.3.0.1] - 2026-07-31

### Fixed
- `ItemsByPath` (the `ILibraryManager` twin) returned **500** on the reference library.
  An unrestricted `GetItemList` dies with `InvalidOperationException: Cannot deserialize
  unknown type` as soon as one row carries a `Type` that no longer resolves to a class -
  a leftover from a plugin that was removed. Both routes now name the item kinds
  explicitly, taken from `IItemTypeLookup.BaseItemKindNames`.
- The restriction is applied to **both** routes on purpose, not only the one that
  crashed: a row the object model cannot load is one the twin can never return, so
  leaving the database route unrestricted would have made the pair disagree - and the
  pair agreeing is this plugin's main quality mechanism. `ItemType` in the response is
  now always a short name, never a fully qualified one.

### Verified
- Full suite against the live server: **18 checks passed, 0 warnings, 0 failures**. Every
  pair agrees item by item, the whole `ItemsByPath` contract holds including the negative
  cases, and `ItemsByPathDB` answers in **6.8 ms** against the ~7 s of the client-side
  enumeration it replaces. The pair comparison for the path routes ran for the first time
  here - before this fix the twin crashed before it could be compared.

## [11.3.0.0] / [12.3.0.0] - 2026-07-30

### Added
- `GET /JFLint/ItemsByPath?path=…` and `ItemsByPathDB`: every item at a path plus
  everything beneath it. The stock API treats `Path` as an output field only, so a caller
  holding a path and needing an item id - which is all `/Items/{id}/Refresh` and
  `DELETE /Items/{id}` accept - had to read the entire item list and match locally, ~7 s
  and ~50,000 rows to identify one item. A name search does not close the gap: the case
  where an id is needed most is a wrong metadata match, and then the item's name has
  nothing to do with its file name.
- `PathItemDto` with `Id`, `ItemType`, `Name`, `Path`. Unlike every other route this one
  is **not** restricted to TV - a movie is what prompted it - and it takes a parameter,
  which makes it the first lookup rather than a finding. Noted in `ARCHITECTURE.md`
  because it departs from the pattern.

### Fixed
- The path filter uses a half-open range (`Path >= p + "/"` and `Path < p + "0"`) rather
  than the obvious `StartsWith`. EF turns `StartsWith` into `LIKE … ESCAPE '\'`, and
  SQLite will not use an index for a LIKE carrying an ESCAPE clause - which would have
  thrown away the `Path` index this route depends on. Read off the generated SQL before
  shipping, not after measuring. Anchoring on the separator is also what stops
  `/Movies/Ring` from swallowing `/Movies/Ring2`.

## [11.2.0.1] / [12.2.0.1] - 2026-07-30

### Fixed
- `SeriesWithoutFilesDB` took **15.8 s**, slower than the `ILibraryManager` route it
  exists to replace, while the other four database routes answer in 13-78 ms. Cause:
  `BaseItems` carries its own index on `ParentId` but **none on `SeriesId`** - read off
  the EF model, not guessed - so the correlated `NOT EXISTS (... WHERE SeriesId =
  series.Id)` cost one table scan per series row, 1,585 of them. Replaced by one grouped
  pass over the episodes that yields both the playable count and the total row count in a
  single scan, plus one small query for the series rows. Measured after the change:
  **45 ms**, best of five, and the answer did not move - the same seven ids, and
  `EpisodeRowCount` still 78 / 42 / five zeros.

### Verified
- All five findings measured against the live server (Jellyfin 10.11.11): **332 / 4 / 6 /
  7 / 1**, matching the expected counts, and every `X`/`XDB` pair returns the identical
  set of item ids - compared item by item with `Compare-Object`. `EpisodeRowCount` was
  compared separately, since comparing ids alone would not have caught an error in it.

## [11.2.0.0] / [12.2.0.0] - 2026-07-30

### Added
- Five library-layout findings, each on two routes as the episode question already is -
  ten in all. `PhantomSeason` (season folder with no readable number that does hold
  files), `SeasonFolderWithoutVideo` (folder yielding no playable episode),
  `DuplicateSeasonNumber` (two seasons of one series sharing a number),
  `SeriesWithoutFiles` (series folder Jellyfin read no file from), `OrphanedItem`
  (season or episode pointing at a row that is gone). Each with a `…DB` twin straight
  off `JellyfinDbContext`; the pair is its own cross-check, since both must return the
  same set item by item.
- `LayoutFindingDto`: one shape for all ten routes, so a caller needs one parser. Carries
  no prose and no numbers inside strings - `SeasonNumber`, `GroupSize`, `EpisodeRowCount`
  and `DanglingId` are typed, and the calling tool composes every sentence the user sees
  from its own language files. Unset fields are omitted by the server's serializer.
- Rows are ordered server-side by series name, then season number, then name, nulls last.
  Identical on both routes, which is what makes comparing a pair a one-liner.

### Fixed
- `Guid.Empty` is treated as "link not set". `BaseItemRepository.Map()` normalises
  `ParentId` to `NULL` but writes `SeriesId` and `SeasonId` raw from non-nullable `Guid`
  sources, so an unset link is stored as all zeroes. Read literally, `OrphanedItem` would
  have reported every seasonless episode - 6 instead of 1 on the reference library - and
  `DuplicateSeasonNumber` would have grouped every series-less season under one key and
  called them duplicates of each other.
- "Beneath" is `ParentId` for the two season findings, not `SeasonId`.
  `Episode.FindSeasonId()` falls back to matching `ParentIndexNumber` against the series'
  children when the file is not in a season folder, so `SeasonId` can name a season the
  file does not physically sit under - which is exactly what `PhantomSeason` looks for.
  Both links give the same numbers on the reference library; the physical one is what
  keeps that true elsewhere.

## [11.1.0.1] / [12.1.0.1] - 2026-07-29

Build metadata only - the compiled behaviour is identical to `11.1.0.0`. The version is
raised because the artifacts for `11.1.0.0` were replaced in place while the reproducibility
work was going on, so the published file no longer matched the one already installed, and
Jellyfin offers no update when the version is unchanged. One version, one artifact.

### Verified
- Both endpoints measured against the live server (Jellyfin 10.11.11, 30,077 episodes):
  `EpisodesWithoutSeasonDB` **28 ms**, `EpisodesWithoutSeason` **2.9 s**, versus **25.4 s**
  for fetching every episode and filtering client-side. All three return the **same six
  item ids** - compared by id with `Compare-Object`, not merely by count. Details in
  [ARCHITECTURE.md](ARCHITECTURE.md).

### Added
- Project scaffolding: git repository, `.gitattributes` (`* -text`, no line-ending
  conversion), `.gitignore`, `CHANGELOG.md`, `HISTORY.md`, `CLAUDE.md`,
  `_git_hooks/pre-commit` + `install-git-hooks.ps1` (changelog guard).
- `Jellyfin.Plugin.JFLint`: plugin project targeting `net9.0` (Jellyfin 10.11.11) and
  `net10.0` (Jellyfin 12.0.0-rc3) from one source tree. The v12 build compiles but is
  **untested** - no v12 server available yet.
- `GET /JFLint/EpisodesWithoutSeason`: episodes whose season could not be determined
  (`ParentIndexNumber IS NULL`), resolved through `ILibraryManager`. Uses only public,
  promised interfaces.
- `GET /JFLint/EpisodesWithoutSeasonDB`: the same question answered straight from
  `JellyfinDbContext`, so the filter runs as SQL and only matching rows leave the
  database. The episode type string comes from `IItemTypeLookup` rather than being
  hardcoded. Both routes require `RequiresElevation`, because the response contains
  file paths.
- `jellyfin.ruleset` taken verbatim from `jellyfin-plugin-template`, so the build
  matches Jellyfin's house style while `TreatWarningsAsErrors` stays on.
- `build.ps1`: publishes each target framework, writes the `meta.json` that Jellyfin's
  plugin manager reads (`targetAbi` `10.11.0.0` / `12.0.0.0`, `status` 0 = Active) and
  packs one installable ZIP per Jellyfin line into `dist\`. Strips everything but the
  plugin's own files from the publish output.
- `README.md` (build, install, endpoints) and `ARCHITECTURE.md` (layout, why two
  endpoints, multi-targeting, package references).
- `manifest.json` for Jellyfin's plugin catalogue, generated and kept up to date by
  `build.ps1`: it adds the freshly built versions, keeps the older entries as release
  history and sorts highest-first. `checksum` is the MD5 of the ZIP - Jellyfin verifies
  it on download and aborts the install on a mismatch. Install URL in the README.

### Fixed
- `PathMap` rewrites the project directory to `/_/`, removing the last absolute path from
  the assembly's debug directory. The build is now reproducible **across checkout paths**,
  not just within one - verified by building the same sources from two differently named
  directories and comparing the assemblies byte for byte.
- `ContinuousIntegrationBuild` was tried and removed again: measured, it does nothing
  here. It normalises paths via `SourceRoot`, which the SCM queries supply - and those had
  to be switched off for commit independence. `PathMap` does the same job without them;
  the reasoning is recorded in the project file so nobody re-adds it expecting otherwise.
- `EnableSourceControlManagerQueries` is now `false` as well. Turning off the version
  suffix alone was not enough: SourceLink still wrote a map containing the HEAD commit
  into the PDB, and the assembly carries its PDB's checksum in the debug directory - so
  the DLL bytes changed even though the commit hash never appeared in the DLL itself.
- `IncludeSourceRevisionInInformationalVersion` is now `false`. The SDK otherwise appends
  the HEAD commit to `AssemblyInformationalVersion` and writes it into the assembly, so
  **every** commit produced a different DLL - including commits that only touched the
  README - and silently invalidated the checksum of an already published release. The
  earlier claim that the compiler was deterministic came from a broken measurement: two
  consecutive builds without a source change do not recompile at all, so the identical
  hashes proved nothing. Verified properly this time, with a commit in between and a
  forced rebuild.

### Changed
- Version numbering now encodes the Jellyfin line in the major version: **`11.x.x.x`
  for Jellyfin 10.11**, **`12.x.x.x` for Jellyfin 12** (was `1.0.0` for both). Two
  entries sharing a version would be decided by array order alone after the `targetAbi`
  filter, and a server upgrading from 10.11 to 12 would never be offered the matching
  build. The version is declared per target framework in the project file; `build.ps1`
  reads it from there, so there is one source of truth.
- ZIP file names drop the ABI suffix - the version already identifies the line:
  `jellyfin-plugin-jflint_11.1.0.0.zip`.
