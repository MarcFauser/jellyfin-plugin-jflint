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
