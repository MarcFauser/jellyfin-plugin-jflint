# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- `ImageInfo`'s remarks now measure the blind spot instead of only naming it, and say plainly
  that a finding count is not a row count. Both came from the first real run: the calling tool
  compared a `COUNT(*)` over the table against the number of findings and read the difference
  as missing rows. That is the natural mistake, because `RowCount` is exactly the field that
  makes the two units look interchangeable - `SUM(RowCount)` within a kind is the row count,
  and nothing else is comparable with the table.
- **The blind spot turned out not to be empty, and a guess made about it here was wrong.** On
  the reference library 75 rows carry no blurhash and 70 are reported, 1 row has no dimensions
  and 0 are reported. The explanation offered from this side - that five rows collapse into a
  duplicated image - was refuted by the reconciliation it came with: `SUM(RowCount)` is 70, not
  75, and the one duplicated image is a person's poster unrelated to the blurhash rows. Six rows
  are genuinely unreachable by **both** halves, which is why the pair still agrees.
- What remains are the two documented causes - an image row belonging to no `BaseItems` row, or
  to one whose `Type` is not in `BaseItemKindNames` - and which it is has not been established
  yet. Recorded as open rather than as either, since the cheap explanation has already been
  wrong once here. Widening the type list is not available as a fix: an unrestricted
  `GetItemList` dies on the first unknown type on this server, which is why the restriction
  exists at all.
- Documentation only, so it travels with whatever release comes next rather than prompting one.

### Fixed
- `build.ps1 -Publish` now pushes the source commit **before** creating the releases, so the
  tags land on the commit that built the artifacts. `gh release create` makes the tag on the
  **remote**, at whatever the default branch points at there; it never sees the local HEAD. The
  guard at the top of the script refuses uncommitted source and says nothing about *unpushed*
  source, so every tag so far named the **previous** release. Measured: 21 of 23 tags in this
  repository point at a tree whose `.csproj` carries the version before theirs.
- **The first reading of that was wrong and is worth recording**: a search for `git tag` found
  nothing and the conclusion was "the script does not tag, so somebody does it by hand". Wrong
  term, and the empty result was taken as a finding. It was settled by watching a live publish
  instead - `v11.17.0.0` landing on the 11.16.0.0 release commit while local HEAD was elsewhere.
- Proven rather than argued: `v11.18.0.0` was published with this push done by hand and nothing
  else changed, and it is the first correctly placed tag in the line - `v11.1.0.0` does not
  count, it was right only for lack of a predecessor. The guard was then forced to fire in both
  directions in a throwaway repository: an unreachable remote aborts before anything is
  published, an already-pushed commit passes silently.
- The push goes **before** the releases and not after, because a tag cannot be moved afterwards
  without a force push - which is the expensive half this avoids. It is also safe in the order
  this script cares about: what must never exist is a manifest naming a release that does not,
  and a source commit advertises nothing at all.
- The **existing** 21 misplaced tags are deliberately left alone. Measured: the download URLs
  carry the tag *name* and no commit, all published artifacts still resolve, and nothing
  automated reads a tag's position - so rewriting them would force-push history on a public
  repository to correct a claim nobody queries.
- `build.ps1` inherited the manifest's `category` instead of writing it. The package header
  comes from the existing `manifest.json`, so the literal in the else-branch is read only when
  no manifest exists at all - a category corrected there would have looked right and done
  nothing. **The file already carried that lesson for `owner`** ("Found the hard way: fixing
  `$Developer` alone changed nothing at all") and it had been applied to that one field only.
  Now written on every run, from a `-ManifestCategory` parameter.
- The parameter is a `ValidateSet` of the eight values Jellyfin's own repository uses, so a
  typo is refused before the build rather than published: an invented category is
  syntactically valid, belongs to no filter, and drops the plugin out of every category view.
- Deliberately **not** tied to the `category` in `meta.json`: that one travels inside the ZIP,
  so changing it changes the artifact and the checksum guard refuses the build. The manifest
  is what Jellyfin groups by - `GET /Plugins` reports no category at all - so correcting it
  there takes effect at once and leaves every published package byte-identical.
- Proved rather than assumed: `-ManifestCategory 'Metadata'` is refused; a normal run leaves
  `manifest.json` byte-identical; `-ManifestCategory 'MoviesAndShows'` really reaches the file,
  which is the run that would have done nothing before. Reported by the poster-overlays
  plugin, which shares this script's ancestry and measured the meta.json half of it.
- `build.ps1` wrote the `meta.json` / `manifest.json` timestamp with a bare `:` in the format
  string, which is not a colon but the placeholder for the **current culture's** time
  separator. Measured on this machine, one instant in three cultures: `de-DE` gives
  `2026-08-23T14:05:07Z`, `da-DK` and `as-IN` give `2026-08-23T14.05.07Z`. 21 installed
  cultures separate time with something else, so the value here was correct only because
  `de-DE` happens to use a colon. Both the parse and the format are now pinned to the
  invariant culture with the separators quoted, and a guard rejects anything that is not ISO
  8601 UTC - forced to fire once against the old expression under `da-DK`. Reported by the
  poster-overlays plugin, which had copied this script.
- `build.ps1` refuses to rewrite a manifest entry whose version is already listed with a
  different checksum. Every run rewrites the manifest, including one without `-Publish`, so
  changing the source without raising the version left a checksum the published ZIP cannot
  redeem - Jellyfin compares the two and refuses to install. **Verified against the live
  repository: all 36 published artifacts were downloaded and their MD5 compared, 36 matched,
  none had drifted.** The guard keeps it that way rather than repairing anything.
- `build.ps1` cleaned the publish output with a flat `Get-ChildItem -File`, while
  `Compress-Archive` packs subdirectories perfectly happily - so anything `dotnet publish`
  wrote into one would have shipped unseen. Two ways in, neither visible at the call site: a
  package with **native** assets (a different asset group, so `ExcludeAssets="runtime"` does
  not touch it) and, more likely, **satellite assemblies** from any package with localised
  resources. Now recursive, directories removed deepest-first, and the timestamp pinning is
  recursive too - a file in a subdirectory would otherwise have kept its build time and cost
  the reproducibility that block exists for. Reported by the poster-overlays plugin, where
  the same flat filter let 86 MB of native copies through.
- **Verified before changing anything: all 36 published packages were downloaded and their
  contents listed.** Every one holds the same five entries - DLL, PDB, XML, `deps.json`,
  `meta.json` - and not a single stray. This was a gap, not a defect.
- Added an assertion on the packed **ZIP** rather than on the staging folder that produced
  it, because only the second one ships and the cleanup can be green while something reaches
  the archive anyway. Forced to fire against a real published package with one satellite
  assembly planted into it: 0 strays before, 1 named stray after.
- **A rebuild without `-Changelog` blanked the changelog of an already published version**,
  found while testing the guard above and worse than the case that prompted it: a wrong
  checksum stops an install with an error, an emptied changelog just leaves the catalogue
  entry blank. The published text is now kept when a run supplies none - and kept loudly,
  since a silent carry-over is how the empty value would go unnoticed again. Supplying
  `-Changelog` still overwrites, which was checked rather than assumed.

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

## [11.25.0.0] / [12.25.0.0] - 2026-09-15

### Added
- `GET /JFLint/ImageInfo` and `…DB` - findings about the rows in `BaseItemImageInfos`: an image
  described by more than one row, an image whose stored width or height is zero, and an image
  with no blurhash. One finding per **image** and check rather than per row, each carrying how
  many rows it covers.
- **Requested as a database-only route in the shape of `MediaInfoDB`, and shipped as a pair
  instead, because the reason `MediaInfoDB` has no twin does not transfer.** That one is alone
  because `MediaStreamQuery.ItemId` is a non-nullable `Guid`, so streams can only be fetched an
  item at a time. Images have no such limit, and nothing on the read path folds duplicate rows:
  `InternalItemsQuery` defaults `DtoOptions.EnableImages` to true, `PrepareItemQuery` adds
  `Include(e => e.Images)`, and the mapper assigns `entity.Images.Select(...).ToArray()` - a
  plain projection with no `Distinct` and no dictionary, on both lines (10.11
  `BaseItemRepository.cs:971-973`, v12 `BaseItemMapper.cs:204-210`). Every row also carries its
  own `Guid.NewGuid()` primary key, so identity resolution cannot collapse them either.
- **And the pair is worth more here than transport, which is the usual objection to one.** The
  database half reads the raw `Path` column and the object-model half reads the materialised
  property, and those are different strings: an image path is written through `GetPathToSave`
  and read back through `appHost.ExpandVirtualPath`. For media that substitution is a corner
  case; for images it is the common case, because downloaded artwork lives under the metadata
  directory. That is where this plugin's one released path defect lived.
- Both halves name every kind from `IItemTypeLookup.BaseItemKindNames`, as `ItemsByPath` does.
  Not a narrowing but a requirement: an unrestricted `GetItemList` dies on the first row whose
  `Type` no longer resolves to a class, and a database half without the same list would report
  rows its twin can never return.

### Fixed
- **A claim written into `ImageInfo`'s own remarks, corrected before release rather than after.**
  The first draft said the image row count is something HTTP cannot answer at all. It is not:
  `BaseItemDto.ImageTags` is a `Dictionary<ImageType, string>` and does hide duplicates, but
  `GET /Items/{id}/Images` walks `item.ImageInfos` and emits one entry per entry in both of its
  passes, so duplicates are visible there. The real argument is the one `MediaInfoDB` already
  rests on - the server can only be asked one item at a time, and that is 164,000 calls on the
  reference library - and the remarks now say that instead. A negative claim about every route,
  drawn from the one route that was actually looked at, is the shape recorded twice on
  2026-09-14; this is the third, and the only one caught before it shipped.

### Verified
- **`Blurhash` is a `byte[]`, not text, and that silently breaks the obvious health query.**
  Read out of the shipped assemblies rather than the source - `Jellyfin.Database.Implementations`
  10.11.11/net9.0 and 12.0.0/net10.0, whose `BaseItemImageInfo` is identical on both: `Blurhash`
  `Byte[]?`, `Path` `String`, `Width`/`Height` non-nullable `Int32`, `ImageType`
  `ImageInfoImageType`. `BaseItemRepository` writes `Encoding.UTF8.GetBytes(BlurHash)`, so an
  empty blurhash is stored as a **zero-length blob** and not as null. In SQLite a blob never
  compares equal to a text value, so `WHERE Blurhash = ''` cannot match even that. Measured in
  memory on sqlite 3.46.1 over three rows: `= ''` returns **0**, `= x''` returns 1,
  `length(Blurhash) = 0` returns 1 - with the control that the same `= ''` against a **text**
  column returns 1, so it is the storage class and not the operator. Both halves therefore ask
  for length, and the object-model half uses `IsNullOrEmpty` because the mapper decodes a
  zero-length blob to `""`.
- **That correction found a real defect and not merely a sloppy operator**, which was not known
  when it was made. The calling tool re-ran its own health query with the length form on the
  reference server and reports **75 empty blobs** among 130,837 rows, against **0** nulls - so
  its published "0 of 130,783 images lack a blurhash" was wrong in substance, not only in
  method. Their measurement, not this project's, and repeated here because it is what turns the
  third finding kind from preventive into live. It also means all three kinds return rows on
  this library from the first run, so the pair gets a real comparison instead of the "0 rows on
  both halves, nothing was compared" case.
- Their conclusion drawn from the old number - that blurhash recomputation could not explain a
  2h19 library scan - happens to survive at 75, since that is 0.06 % of the rows. It is recorded
  as surviving rather than as confirmed: it had been resting on a measurement nobody had made.
- **Every query shape was checked for translation on both EF Core lines before it was written,
  with a negative control.** This project has a scar exactly here: `Contains('.')` once compiled
  clean, translated on EF Core 10 and threw at query time on the whole EF Core 9 line Jellyfin
  10.11 ships. The grouped count, the join onto the grouped result, the filter applied *after*
  that join on a projected property, the `Where` before the `GroupBy` and the blurhash length
  test were each generated **and executed** against SQLite on EF Core 9.0.11 and 10.0.11. All
  produce identical SQL on both. `Contains(char)` ran in the same probe and failed on 9 while
  succeeding on 10, so the green results are not a blind instrument.
- **The read-side half of jellyfin/jellyfin#17192's analysis does not reproduce, and saying so
  matters because it would otherwise read as a reason to distrust the object-model half.** That
  write-up names three factors and the first is "duplicate materialization on read" -
  `AsNoTracking()` plus `AsSingleQuery()` plus several collection `Include`s allegedly yielding
  `|Images| x |other collections|` copies. Measured on a seeded model with all four collections
  Jellyfin includes: the join really does return **24 rows for 2 images** (checked with raw SQL,
  so the control fires) and EF still materialises exactly **2**, on EF Core 9.0.0, 9.0.11 and
  10.0.11 alike. The write-up is flagged as AI-generated by its own author, who says it was
  never run. Its other two factors - delete-then-insert without deduplication, and a random
  surrogate key that lets duplicate inserts through - are untouched and explain why duplicates
  **persist**; where they come from is unsettled, and this route does not need to know.
- The issue's figures were read in the issue rather than taken second hand: *"I have one movie
  with 262,144 image rows in my DB with 4 real images"* and *"If I clean them up manually the
  library scan becomes fast again"*, both `Gr3q`, 2026-07-05. The same reporter adds a day later
  that after cleaning he **could not reproduce** the growth on 12-rc2.
- **The duplicate check has no shelf life on either line: nothing at the schema level prevents
  duplicate rows.** Jellyfin 12 adds a `BaseItemImageInfoConfiguration` that 10.11 does not
  have, and it declares `HasIndex(e => new { e.ItemId, e.ImageType })` **without** `IsUnique`.
  A first reading of this said 10.11 has no index on the table at all; that is wrong and was
  caught by re-reading the migration - 10.11 carries the single-column
  `IX_BaseItemImageInfos_ItemId` that EF's foreign-key convention produces, and
  `20260206224832_IndexOptimizations` **drops** it on v12 in favour of the composite one. The
  performance conclusion first drawn from the wrong version - that the grouped query is a full
  scan on 10.11 - is withdrawn rather than corrected, because no query plan was ever measured
  to support it either way.
- Findings are grouped rather than emitted per row, and that is what gives the pair a unique
  key. The object-model half cannot see a row's primary key - the mapper drops it - so per-row
  findings about identical rows would tie on every field, and the two halves could agree on the
  set while differing in order. That is the fault `SortEpisodes` and `SortMovies` were fixed for
  in 11.15.0.0, and here no tiebreaker exists to fix it with.

## [11.24.0.0] / [12.24.0.0] - 2026-09-14

### Fixed
- **The `uniqueid` mechanism from the previous release is right; the evidence given for it was
  not, and it is removed.** That entry said the library's 9,575 TvRage ids proved NFOs can
  write an unregistered provider key. They do not come from NFOs. Jellyfin's **built-in** TMDb
  provider writes them from TMDb's `external_ids` cross-reference: `TmdbEpisodeProvider` sets
  Tvdb, Imdb and TvRage - and **no Tmdb id of its own** - which is exactly the key set those
  episodes carry (Tvdb 30,758, Imdb 26,524, TvRage 9,575, Tmdb 0). At series level the same
  provider does set its own id, and there all 166 TvRage rows carry a Tmdb id and none lacks one.
- The inference that failed was **"no TvRage plugin was ever installed, so only the NFO path
  remains"**. Any provider may write any key; a missing plugin of that name proves nothing. The
  implausible-looking value quoted as support (`1065779330`) proves nothing either - the two
  NFOs beside those files contain no `uniqueid` element and no `tvrage` at all, and TVRage did
  issue episode ids in that range.
- **An NFO can set and overwrite a provider id but never remove one.** An empty element is not a
  deletion: `<uniqueid type="X"/>` exits at the `IsEmptyElement` guard and
  `<uniqueid type="X"></uniqueid>` reaches `TrySetProviderId` with an empty value, which returns
  false. The parser contains no `Remove`, no `Clear` and no wholesale `SetProviderIds` call -
  which is what leaves removal to this plugin.
- **The `type` spelling must match exactly, and a wrong one is not cosmetic.** Normalisation
  only covers the seventeen names in `MetadataProvider` - which includes `TvRage` but not
  `AniDb`, `AniList` or `AniSearch` - so `type="anidb"` creates a **second** entry beside
  `AniDB`. Jellyfin does not care, but the payload then carries two keys differing only in case,
  and PowerShell's `ConvertFrom-Json` rejects the **entire document** with "contains keys with
  different casing". One such row takes down every query that returns it, at HTTP 200 and with
  valid JSON. `-AsHashtable` reads it; the row still has to be repaired.

### Changed
- The tally kept in `11.23.0.0` was too narrow. It recorded that a **negative** claim about a
  host ages. Its twin is the same error in green: **"this is the only way" is also a statement
  about every other way**, drawn from the single one that was observed. Three in a day, two
  negative and one positive, all the same shape. The cheap guard before passing one on: *which
  second way would have to exist for my sentence to be false, and did I look there?*

## [11.23.0.0] / [12.23.0.0] - 2026-09-14

### Fixed
- **The previous release said the NFO is a dead end for these keys. It is not**, and the
  correction is in the same place the claim was. `BaseNfoParser` has **two** consumers of its
  element map, and only one of them refuses an unknown provider:
  - the element-name branch looks up `<anidbid>` and calls `reader.Skip()` - nothing is stored;
  - the `<uniqueid type="anidb">` branch looks the **type attribute** up in the same map and,
    finding nothing, **stores it verbatim**. No registration, no validation.
- **Found by a question rather than by reading**, which is why it was missed twice: the owner
  said he had never installed TvRage and that the server is only months old, so the 9,575
  TvRage ids on his episodes could not be historical. They came in through `uniqueid` tags in
  release NFOs. One of them reads `1065779330`, a number no such database ever issued - the
  value is whatever the NFO author wrote. `TvRage` and `AniDB` are both stored and both
  unregistered, and no TvRage plugin has ever existed here, so that branch is the only way in.
- The route's remarks now carry the consequence for a caller as a **warning**: a value set
  here is a database edit, the file beside the media still says what it said, and a later
  rescan will put the old value back through the same `uniqueid` path. Repairing the file is
  the durable fix; the route is the one that takes effect now.

### Changed
- This is the second time in one day that a claim of the form "there is no other way" turned
  out to be false, both taken over from a sibling session without being checked. Recorded as
  the shape rather than the instance: **a negative claim about a host's capabilities is a
  measurement, and it ages** - `11.22.0.0` corrected the first half of it and inherited the
  second.

## [11.22.0.0] / [12.22.0.0] - 2026-09-14

### Fixed
- `SetProviderId`'s own remarks claimed it was **the only way** to change a provider id. It is
  not, and the claim shipped in the previous release's catalogue text, which is why this one
  exists rather than waiting: `POST /Items/{itemId}` calls `item.SetProviderIds(...)`, which
  rebuilds the dictionary from the body, so a key left out of it is gone. Verified against this
  server's OpenAPI - the route is present and `BaseItemDto.ProviderIds` is a string map.
- **The reasoning that replaces it is narrower and is the real one.** `UpdateItem` writes
  **25 fields straight from the body with no null guard** - counted in
  `ItemUpdateController.cs`, and they include `Name`, `Overview`, `Genres`, `LockedFields` and
  `IsLocked = request.LockData ?? false`; only `ProviderIds` itself is guarded. A partial body
  blanks the rest, so a correct call means reading a full DTO, changing one entry and writing
  it all back, having first proved that `GET /Items/{id}` returns all 25 faithfully. A route
  that can touch nothing else needs no such proof.
- That is the argument `DeleteItemKeepFile` already rests on, word for word: **not "the host
  cannot do it", but "the host's way takes more with it than I want to touch"** - and the
  difference between that and rebuilding something out of ignorance is that the reason can be
  written down.
- The false sentence is left standing in the `11.21.0.0` entry with the correction beside it
  rather than edited away. A quietly fixed claim teaches nobody, and this one was taken over
  from a sibling session without being checked - which is the part worth remembering.

## [11.21.0.0] / [12.21.0.0] - 2026-09-14

### Added
- `POST /JFLint/SetProviderId` - sets one provider id on named rows, or removes it when no
  value is given.
- **CORRECTION, same day: this entry first said "the only way these values can be changed at
  all", and that is wrong.** Jellyfin's own `POST /Items/{itemId}` (`UpdateItem`) replaces the
  whole provider dictionary - `item.SetProviderIds(request.ProviderIds)`, which rebuilds it from
  the body - so a key left out of the body is gone. Measured against this server's OpenAPI:
  the route is there and `BaseItemDto.ProviderIds` is a string map. The claim is left standing
  and corrected rather than rewritten, because the reasoning that replaced it is the point.
- **What is true is narrower and is the actual argument.** `UpdateItem` writes **25 fields
  straight from the body with no null guard** - `item.Name`, `item.Overview`, `item.Genres`,
  `item.LockedFields`, `item.IsLocked = request.LockData ?? false`, `RunTimeTicks`,
  `Video3DFormat` and eighteen more, counted in `ItemUpdateController.cs`. Only `ProviderIds`
  itself is guarded. A partial body therefore blanks the rest, and a correct round trip would
  have to read a full DTO, change one entry and write it all back - having first proved that
  `GET /Items/{id}` returns every one of those 25 faithfully. **A route that can only touch
  provider ids needs no such proof**, which is the same argument `DeleteItemKeepFile` rests on:
  not "the host cannot do it", but "the host's way takes more with it than I want to touch".
- The NFO is a dead end for these keys, and that part holds: `BaseNfoParser` builds its set of
  readable elements from `ProviderManager.GetExternalIdInfos` plus four hardcoded TMDb/IMDb keys
  and calls `reader.Skip()` for anything else. Measured on the reference server - a Series
  reports seven external ids and **no anime provider**, because those plugins are uninstalled,
  so an `<anilistid>` element is skipped on every read however often the file is rescanned.
- **Setting beats removing, and that is measured too.** A non-numeric value such as `none`
  suppresses a provider's name search exactly as `-1` did - AniDB's series provider gates on
  `string.IsNullOrEmpty` and never parses the id on that path, read at the source - while a
  consumer that reads ids numerically drops it instead of sending it. Removing the key also
  satisfies the consumer but gives up the suppression, and for one series here the name search
  lands on an unrelated 1988 short.
- A blank value means remove, and it has to: Jellyfin will not store an empty id
  (`TrySetProviderId` returns false, `SetProviderId` throws, `IsValidProviderId` rejects it), so
  the removal goes through `ProviderIds.Remove`. A `none` survives a later merge because
  `IsValidProviderId` passes any non-blank value for a provider with no registered validator -
  only Imdb, Tmdb, TmdbCollection, AudioDb and MusicBrainz have one.

### Changed
- **The finding routes now ask whether a value will harm a consumer, not whether it could
  identify a title.** The old predicate was per provider and numeric; the new one is
  provider-independent and asks only whether the value *parses as a number and is not a usable
  one*. Provider ids are strings, and the harmful class is the one that looks like a number:
  SkipMe.db parses with `int.TryParse` and **no sign check**, then omits a null field entirely,
  so `-1` and `0` are sent and refused with 400 while `none` never leaves the process.
- That removes the need for a list of accepted sentinel words, which would have had to be
  guessed and would have flagged the next word somebody picked. It also explains why "set it
  to 0" looks like a fix and is not one.
- `GroupingKeyRule` keeps the **identity** question unchanged - all thirteen of its vectors
  still hold, verified against the built assembly. The two questions genuinely differ and now
  say so: `Tvdb=abc` identifies nothing but harms nobody, `TvRage=0` does both.

### Verified
- Both predicates measured against the live library over all 34,606 movie, series and episode
  rows: **1493 values** either way. The breakdown is the finding - `TvRage=0` on 1297 rows
  (1281 of them episodes), the anime sentinels at 194, `Tvdb` twice. The 192 reported earlier
  were series alone.
- Fourteen vectors for the new predicate and thirteen for the old, both directions, including
  the four strings that must pass (`none`, `skip`, `-`, a Tvdb slug) and the two that must not.

## [11.20.0.0] / [12.20.0.0] - 2026-09-14

### Added
- `GET /JFLint/InvalidProviderIds` and `…DB` - provider ids that cannot identify anything,
  one row per **id** rather than per item, for movies, series and episodes.
- `POST /JFLint/RemoveProviderId` - removes named providers from named rows. **Jellyfin never
  deletes a provider id on a refresh**: `MetadataService.MergeBaseItemData` walks the source and
  writes into the target, so an id the source no longer carries is simply left standing.
  Cleaning an NFO is therefore only half the repair. Setting it empty is not available either -
  `TrySetProviderId` returns false on a blank value and `SetProviderId` throws - so removing the
  key is what is left.
- **The route can do exactly one thing.** It takes ids from the caller, which got them from the
  query pair and showed them to a user; it cannot touch a file, delete an item, write another
  field or choose its own targets. Same reasoning as `DeleteItemKeepFile` taking one id instead
  of a filter: a wrong predicate upstream costs the rows that were on screen, not the library.
- The response lists **what was removed, with its value**, rather than a count - that is the
  material to put it back, and it keeps "nothing matched" distinguishable from "all matched".

### Changed
- The per-provider formats moved into a shared `ProviderIdRule`; `GroupingKeyRule` now delegates
  to it instead of restating them. Verified that all thirteen existing grouping-key vectors keep
  their verdicts **and their exact messages** - two rules that look alike are two rules that
  drift apart, and this one had already been copied once.

### Verified
- **"Not a positive integer" is the wrong predicate, measured rather than argued.** Asked of
  every provider on the reference library it reports **678 values across 512 of 527 series** -
  `TvdbSlug` is text by design (515 of them), `TvdbCollection` names a group, `Custom` is opaque.
  Asked per provider it reports 192 across 13 series, which is the real fault. The rule is a
  positive list and stays silent on anything it does not know.
- **The ids a caller sees are not the ids it must repair.** On v12 the merged view shows 13
  series where the database holds 85 release-folder rows, and the values sit on the rows.
  Passing the merged ids would repair 13 of 85 and leave the fault in place, so both query
  routes report row ids and the sort carries `ItemId` as a real tiebreaker - one merged series
  produces many rows with the same name and provider.
- Counts reconciled against the sibling tool rather than accepted: 13 series and 85 folders
  agree exactly, and its total of 277 is **192** here. `AniList` (81) and `AniSearch` (26) match
  to the row; `AniDb` is 85 here against 170 there, exactly double, which is what matching both
  `AniDb` and `AniDB` against a case-insensitive dictionary produces.

## [11.19.0.0] / [12.19.0.0] - 2026-09-10

### Fixed
- `MediaInfoDB` reported **1118 Dolby Vision files as `DOVIInvalid`** on a Jellyfin 12 server
  while the server itself answered `DOVIWithHDR10` / `DOVIWithHDR10Plus` for the same items.
  The route now also reads `ColorSpace` and `ColorPrimaries` and fills them into the
  `MediaStream` before asking Jellyfin to classify it.
- **Jellyfin 12 added a validation step to `GetVideoColorRange()`**: once a Dolby Vision profile
  is derived, the result is downgraded to `DOVIInvalid` unless the stream also carries
  `bt2020nc` and `bt2020`. This route filled the eight fields the 10.11 implementation read and
  no more, so on v12 every DV file failed that new test.
- Proven rather than deduced, offline against both package lines with identical inputs:
  `10.11.11` answers `DOVIWithHDR10` with and without the two fields; `12.0.0` answers
  `DOVIInvalid` without them and `DOVIWithHDR10` with them. **Not a v12 branch in the code** -
  two columns that should always have been read, harmless on the older line.
- Confirmed the data supports it before shipping: eight sampled misclassified rows all carry
  `bt2020nc` / `bt2020` in the database, and a control SDR row carries neither.

### Changed
- The `net10.0` build now compiles against the **final `12.0.0`** packages instead of
  `12.0.0-rc3`, with rc5, rc6 and rc7 in between. Both targets build clean with
  `TreatWarningsAsErrors`, and the `MediaStream` / `MediaStreamInfo` property sets are identical
  between rc3 and final - what changed was the derivation logic, not the schema.

### Verified
- **First execution of the v12 line, ever.** Eighteen artifacts had been published and none had
  run. Plugin `Active`, 23 routes registered, every route pair returning the same set, and the
  virtual-path work carried over untouched: 102,608 rows under `%MetadataPath%` expanded
  identically on both halves, ancestor check over 102,824 rows agreeing.
- **A route with no twin has the server as its twin, and it must actually be asked.** This
  defect was invisible to every check the suite runs on it - shape, uniqueness, "the derivation
  ran", all green on 1118 wrong rows. Calling the server's own method guarantees the same
  verdict for the same stream; it guarantees nothing about the inputs being complete. The
  remarks now say so where the "cannot drift" claim is made.
- `DuplicateEpisode`'s two halves disagreed on 29 ids, and it is **not a defect in either**:
  Jellyfin has merged those files as alternate versions, so the database half sees two rows and
  the library half sees one item with two sources. 16 of the 29 carry a `PrimaryVersionId`, the
  other 13 are their primaries with `MediaSources` 2 or 3. On 10.11 no row carried a link at
  all. Left as it is - the two halves answer different questions here, and which is wanted is a
  design decision rather than a bug.

## [11.18.0.0] / [12.18.0.0] - 2026-09-03

### Changed
- `FileNameTitleRule.LooksLikeAFileName` now also reports **hyphen**-separated names, ported
  verbatim from the calling tool where it was added the same day. The rule knew only the dotted
  form, so a release like `tvr-lots-s02e01` or `tmsf-highscore-s01e01` carries no dot, does not
  equal its leaf either once Jellyfin has stripped `-1080p`, and was invisible.
- **The hyphen half additionally requires lower case, and that one condition is what makes it
  usable.** Measured upstream over 44,528 titles: hyphens alone add 85 rows of which 19 are real
  German episode titles - `Gute-Nacht-Geschichten`, `Kopf-An-Kopf-Rennen`,
  `Papier-Blüten-Träume`. A German compound is capitalised and a scene release is not, and that
  separates the two sets without exception. With the condition the same measurement gives 66,
  every one a release name. The accepted false positive is `ai-mai-mi`, named in the vectors so
  nobody later reports it as a defect.
- The condition applies to the hyphen half **only**. A dotted release name is usually
  capitalised (`Mr.Robot.S03E02.German…`), so the same test on the dotted half would empty the
  finding. One vector holds each side of that.

### Fixed
- The database half's SQL pre-filter would have dropped exactly the rows the new branch adds.
  It admitted a name containing a dot, or one that is its own path leaf; a hyphen-separated
  name is neither. The library half pre-filters nothing, so the two would have disagreed - and
  it would have read as a defect in the port rather than in the `WHERE`. **A filter one half
  applies and the other does not is how a pair stops being a control**, which this file says of
  the twin routes and had quietly broken in its own query. Found before shipping by asking what
  the pre-filter admits, not by running it.

### Verified
- The ported rule was run over all 44,561 library items and reports **232** - the same number
  the calling tool's independently written fallback computes over the same library. Two
  implementations agreeing to the row is stronger than either alone, and it is what the port
  exists to preserve.
- Eleven vectors for the shape itself, including all three German compounds, the accepted false
  positive, the dotted capitalised name that must stay reported, and the floors. The control is
  one string in two spellings - `abc-def-ghi` reported, `Abc-Def-Ghi` passed - which is the
  sharpest form available for a condition that turns on case alone.
- `PerEpisodeFolder` shares the predicate and was checked rather than assumed: it stays at 0,
  because only seven season rows on this library have a path at all.

## [11.17.0.0] / [12.17.0.0] - 2026-09-03

### Added
- `GET /JFLint/EpisodeShapedMovie` and `…DB` - an item Jellyfin resolved as a film whose file
  name carries a season and episode number. A **mixed** library, one with no `CollectionType`,
  has Jellyfin decide per folder whether it holds a film or a series, and for a season folder it
  sometimes decides "film"; every episode then becomes its own movie.
- **Why it is not covered by an existing route.** Until now this was visible only where those
  episodes happened to resolve to one provider id and so surfaced as a duplicate group. Measured
  here: four such folders exist, `DuplicateMovie` sees three of them - the fourth produces no
  collision at all and was invisible. *A check that finds a fault only where it happens to trip
  another check is not a check on that fault.* Raised by the calling tool's session, which
  measured the fourth folder independently.
- **Nor is it a stricter `FileNameTitle`.** That asks whether a title is only a file name, this
  asks whether the **type** is wrong; 16 of the 22 rows here are reported by both, and the two
  want different repairs - a rename against moving the folder into a series library. The six
  reported *only* here are the argument for the kind: their names are hyphen-separated, and the
  dotted-name rule cannot see them. That gap in `FileNameTitleRule` is real and is deliberately
  **not** fixed on this side - the file is a verbatim port, and its two halves are a control for
  the calling tool only while all three agree on every row. It has been reported upstream.

### Verified
- **The judgement is on the file name, not the folder, and that was measured rather than
  chosen.** Across 2368 films a folder criterion (`Sxx` without an episode number, which is what
  a season folder carries) would have added exactly two rows, and both are wrong:
  `Gintama.S00.The.Movie.1` and its sequel are genuine films whose release group used `S00` as a
  specials marker. Two false and none true is not a trade. Both are kept as suite vectors so the
  idea is not reintroduced.
- The pattern accepts the separated conventions `S01.E01` and `S01-E01`, which the shorter
  `S\d{1,2}E\d{1,3}` token already in `FileNameTitleRule.Evidence` misses, and refuses a match
  embedded without boundaries, which is where false positives begin. On this library the two are
  **exactly equal** - 22 rows either way - so the stricter form costs nothing now and covers more
  later. Recorded because two season/episode patterns now exist in the source for different jobs,
  and that is a decision rather than an oversight.
- The compiled rule was run against all 2368 films and reports **22**, matching an independent
  measurement made in PowerShell before the code existed. Fourteen vectors cover both directions,
  both path separators, and the two folder-criterion false positives.
- The database half runs the whole judgement in memory on purpose. The rule is a regular
  expression, which SQLite cannot be handed, and any `LIKE` narrowing added to pre-filter would
  be a second, looser rule applied by one half only - which is how a pair stops being a control.
  The stored path is expanded before the rule sees it, since the other half is handed a path
  Jellyfin has already expanded.
- The predicate is shared with the calling tool, which needs its own copy for the third stage of
  its fallback. The text was sent there to be adopted verbatim rather than written twice: one
  side owns it, the other copies. Two independently written rules for one question drift, and
  the pair exists to detect drift, not to create it.

## [11.16.0.0] / [12.16.0.0] - 2026-09-02

### Added
- `GET /JFLint/ImplausibleGroupingKey` and `…DB` - series merged onto a grouping key that
  cannot be a real provider id. **Sharing a key is normal and is not reported**: it is how one
  series spread over several release folders stays one series, and seventeen keys do that
  legitimately here. Reported is a shared key built from an id that could not identify
  anything - which is what happens when a sentinel is written into two NFOs to *prevent* a
  merge and matches itself instead. Jellyfin logs such a merge with no line at all.
- **The judgement is on the row's provider id, never on the key.** `Series.GetUserDataKeys`
  inserts Imdb, then Tvdb, then Custom at position 0, so the leading id is Custom if present,
  else Tvdb, else Imdb; `CreatePresentationUniqueKey` then appends the metadata language and
  every library folder guid. The key is a composite and cannot be split back apart - a custom
  id may contain hyphens itself, as `v-1984-final-battle` does. Read from the source, not
  assumed.
- **Plausible means something different per provider, and that is the point of the rule.**
  Tvdb ids are positive integers, so `-1` and `0` are impossible; Imdb ids are `tt` plus
  digits; a **custom id is opaque by design and is never reported**. A "looks odd" filter
  would have flagged every legitimate custom merge group from the day that plugin was
  installed - the kind of false alarm that gets a check ignored. Where none of the three
  provider ids led the key, the rule stays silent rather than judging what it cannot see.

### Verified
- The route answers **0** on this library and always will while every merge on it is
  legitimate, so the rule is exercised directly against the built assembly instead: thirteen
  vectors, both directions, including the two that keep it honest - `Custom` with
  `v-1984-final-battle` and `Custom` with `-1` must both pass, the second being the very value
  that caused the original merge when it sat in a `Tvdb` field.
- A route whose only observed answer is zero proves nothing about itself. That is said in the
  suite rather than left implied.
- The two halves are a real pair here rather than two transports of one answer: the library
  half asks each series to **compute** its presentation key, the database half reads the
  **stored** one. Agreement means the two are in step; disagreement would be a finding.

## [11.15.0.0] / [12.15.0.0] - 2026-09-01

### Fixed
- `SortEpisodes` and `SortMovies` ended on `Path`, which is not a unique key - **two items on
  one file is precisely what a duplicate finder exists to surface**. Rows tying on every key
  fall back to whatever order the source produced, which differs between the two halves and
  quietly makes the pair incomparable. Both now end on `Id`; the other four sorts already did.
- Found because the sibling tool hit the same class with a key that ties for *every* episode
  of a series: **invisible while one source existed, obvious the moment a second one did** -
  its two fallback stages returned the same 145 findings in different orders, same size, same
  content, different hash. The two shapes together are the point: theirs ties systematically
  and would eventually have been noticed, ours ties rarely and would not have been.

### Verified
- **The suite never checked the order, and now does.** It compared the two halves as *sorted*
  id lists - a set check, blind to exactly the fault the shared comparers were added for in
  11.12.0.0. Measured: `Compare-Object` on `1..5` against `5..1` reports **0** differences.
  A sequence comparison sits beside the set one, and all eight pairs pass both.
- That measurement is itself a check in the suite rather than a comment, so the day the
  behaviour changes, the note reports itself instead of quietly becoming wrong.
- Honest limit: five of the eight pairs return no rows, where an order check proves as little
  as any check on an empty set. It carries weight for `PhantomSeason` (45), `FileNameTitle`
  (316) and `PerEpisodeFolder` (45).

## [11.14.0.0] / [12.14.0.0] - 2026-09-01

### Added
- `GET /JFLint/MediaInfoDB` - `Id`, `ItemType`, `Name`, `SeriesName`, `Path`, `Width`,
  `Height`, `VideoRange`, `VideoRangeType` for every movie and episode with a video stream,
  in one join. **`BaseItemDto` carries no `VideoRange`** - measured against the running
  OpenAPI, 153 properties and neither of the two - so the only stock route to it is
  `Fields=MediaStreams`, which ships every stream of every item: 76 s and 117 MB for the
  episodes alone, against 15 s and 29 MB for `Fields=Path,Width,Height`.
- The classification is **Jellyfin's own**. Dolby Vision profiles 5/7/8/10, the RPU and
  base-layer flags, the compatibility id, HDR10+, the `dovi`/`dvh1`/`dvhe`/`dav1` codec tags
  and the `smpte2084`/`arib-std-b67` colour transfers are all read by
  `MediaStream.GetVideoColorRange()`, which is public. The eight columns it reads are filled
  into a `MediaStream` and the method is called; nothing here reimplements the rule, so it
  cannot drift from the server's answer. The field list was taken from the whole method,
  lines 807-878, not from the part that fitted on a screen.

### Changed
- **This route deliberately has no twin, and the reason is written into it.** Every other
  query here exists twice so each half checks the other. That is not possible for this one
  and would be worth little if it were:
  - *Not possible*: `MediaStreamQuery.ItemId` is a non-nullable `Guid` and
    `MediaStreamRepository.TranslateQuery` filters on it unconditionally, so
    `IMediaSourceManager.GetMediaStreams` can only be asked one item at a time - tens of
    thousands of calls, each opening its own context, which would be **slower than the 76 s
    fallback it exists to back up**.
  - *Worth little*: both halves would end in the same `GetVideoColorRange()`. A second
    transport of one derivation is not a second opinion about it, and a pair that shares its
    source proves transport rather than truth.
  A caller therefore falls back to `Fields=MediaStreams`, not to a second route.
- Only the first video stream per item is reported, ordered by `StreamIndex`. An item may
  hold several, and reporting each would put one id into the answer more than once.

## [11.13.0.0] / [12.13.0.0] - 2026-08-22

### Fixed
- **`ItemsByPathDB` still disagreed with its twin at any path that *contains* the metadata or
  data directory rather than lying inside it, and the two previous releases said otherwise.**
  `ReverseVirtualPath` is an unanchored `Replace`: it rewrites a target at or below those
  directories and leaves an ancestor untouched, so the range was built around the real path
  and could not reach a column value beginning `%MetadataPath%`. Measured on 11.11.0.0:

  | path | DB twin | library twin |
  |---|---:|---:|
  | `/var/lib` | 10 | 99220 |
  | `/var/lib/jellyfin` | 10 | 99220 |
  | `/var/lib/jellyfin/metadata` | 99014 | 99014 |
  | `/var/lib/jellyfin/data` | 196 | 196 |
  | `/var/lib/jellyfin/root` | 10 | 10 |

  99014 + 196 + 10 = 99220 exactly - the missing rows were precisely the placeholder-stored
  ones. **This was the worse kind of defect than the one it replaced**: 0 against 99005 is
  visibly broken, 10 against 99220 is well-formed and plausible.
- No single range can cover it, so the shape changed rather than the arithmetic. Under the
  column's BINARY collation those rows sit in disjoint stretches with the media library
  sorting between them - `%AppDataPath%/…` < `%MetadataPath%/…` < the media root <
  `/var/lib/jellyfin/root/…` - so one half-open range spanning the outermost two would return
  the entire library. The route now scans **one range per stored root**: the target in its
  stored spelling, plus each placeholder whose real directory lies strictly below the target.
  An ordinary media path yields exactly one root and issues exactly one query, as before.
- **The two halves labelled one row differently.** Same id, same name, same path:
  `ManualPlaylistsFolder` from the library half against `PlaylistsFolder` from the database
  half, because `GetBaseItemKind()` parses the client-facing `GetClientTypeName()` while the
  twin reads `ItemTypeLookup`. Both halves now name a row from that one table.

### Verified
- The multi-range design was checked at id level against the live server before it was
  written: the ranges do not overlap, and their union is exactly the set the library half
  returns - 0 rows only on one side, 0 only on the other.
- The label fix was measured with a control: one mismatch under the data directory, and zero
  across 447 rows of a real movie folder.

### Changed
- **Two claims in the previous releases were wrong and are corrected here.** The comment
  added in 11.11.0.0 - that the halves "already drew the same rows, they just spelled Path
  differently" - is false: at `/var/lib/jellyfin` they drew 99220 and 10, and those rows were
  unreachable, not differently spelled. The catalogue text of 11.11.0.0 said the database
  route "matches ItemsByPath again", which held only at or below the placeholder directories.
- The placeholder spellings are now a named two-entry list with the reason it cannot be
  derived: `IServerApplicationPaths`, the interface that names them, is never registered with
  the container - `ApplicationHost` registers that instance as `IApplicationPaths` alone - so
  a controller cannot ask for it. Checked, with the registration of `IServerApplicationHost`
  as the positive control, since that is the one this controller already depends on. The
  entries are self-checking: a placeholder the server does not substitute expands to itself
  and is dropped.

### Added
- A suite check for the ancestor path, derived from `ProgramDataPath` rather than written in,
  so it also holds on a Windows host or a relocated data directory - with a control that the
  ancestor really does cover the placeholder rows, since otherwise its agreement would be
  agreement about nothing.

## [11.12.0.0] / [12.12.0.0] - 2026-08-22

### Fixed
- **The placeholder trap was fixed in one route and left in nine.** The previous release
  taught `ItemsByPathDB` that Jellyfin stores `%MetadataPath%` and `%AppDataPath%` rather than
  real paths, but every other database half still emitted the raw column while its library
  half emitted the expanded one. Not reachable on this library today - measured, zero Series,
  Season, Episode or Movie rows carry a placeholder - but the `Collections` library already
  sits under the data directory, and in `DuplicateEpisode` and `DuplicateMovie` `Path` is the
  last sort key, so the pair would have differed in row *order* as well as in the string.
  Every database half now expands what it read, through a single `StoredPath` helper rather
  than a call copied to each site.
- `FileNameTitleDB` expands the path **before** handing it to `FileNameTitleRule.Evaluate`,
  not merely before reporting it. The twin feeds the rule a materialised item's path, and the
  same decision must not be made on two different inputs.
- **`EpisodesWithoutSeason` was the only pair with no shared comparer**, so it could not be
  compared element by element - which is the only reason a pair exists. This is not the same
  as both halves being unordered: the library half silently inherits Jellyfin's default
  `OrderBy(SortName)` from `BaseItemRepository.ApplyOrder`, while the database half had no
  `OrderBy` at all and took SQLite's scan order. Both now run through one `Sorted`.
- The same pair reported an unset series link two ways - the library half as an all-zero guid,
  the database half as `null`. Both now report `null`.

### Verified
- Both target frameworks build clean, 0 warnings under `TreatWarningsAsErrors`.
- The sweep was checked rather than assumed: all eight `row.Path` uses in
  `LibraryLayoutController` were confirmed to sit inside `*FromDatabaseAsync` methods before
  anything was replaced, and `ItemRemovalController` and `CacheController` were confirmed to
  touch no database at all, so their paths are already expanded.
- **The ordering fix cannot be demonstrated on this library**, because the route returns zero
  rows here and a zero-row comparison cannot detect an ordering difference. It rests on the
  source, and that is said plainly rather than dressed up as a passing test.

### Added
- A suite check that asks `ItemsByPathDB` for the stored spelling and requires every returned
  `Path` to come back expanded, with a control proving the check fires on a placeholder, plus
  a comparison that both halves report identical `Path` strings and not merely identical ids.
  This check was failing before the previous release.

## [11.11.0.0] / [12.11.0.0] - 2026-08-22

### Fixed
- **`ItemsByPathDB` answered 0 for everything below the metadata and data directories,
  while `ItemsByPath` answered in full.** The two routes are a pair precisely so each is the
  other's cross-check, and that promise was broken. Jellyfin does not store the path it hands
  out: on write it swaps those two directories for the placeholders `%MetadataPath%` and
  `%AppDataPath%` (`BaseItemRepository.GetPathToSave` -> `IServerApplicationHost.ReverseVirtualPath`)
  and swaps them back when it materialises the item. The library twin therefore filtered
  expanded paths while the database twin compared the caller's real path against the stored
  form. The caller's path is now put into the stored form before it is compared, and every
  returned `Path` is expanded back - which is how Jellyfin itself filters by path.
- Both routes now also accept the placeholder spelling of a directory and answer the same for
  either form.

### Verified
- Measured on the reference server **before** the change, counted on the raw response text
  because `@(ConvertFrom-Json '[]')` reports 1 for an empty body and would have faked a hit:

  | path | DB twin | library twin |
  |---|---:|---:|
  | `/var/lib/jellyfin/metadata` | 0 | 99005 |
  | `/var/lib/jellyfin/data` | 0 | 193 |
  | `%MetadataPath%` | 99005 | - |
  | `%AppDataPath%` | 196 | - |
  | a real movie folder (control) | 447 | 447 |

- **Nothing was ever missing from the database.** Asking the unfixed route for
  `%MetadataPath%` already returned the same 99005 the twin reported for the expanded path -
  same rows, different spelling. That equality is what identified the cause.
- The control that must stay unchanged: the movie folder answers 447 on both routes with
  identical id sets, and has to keep doing so.
- Measured **after** the change, on 11.11.0.0, with the id sets compared element by element
  rather than only the totals:

  | path | DB twin | library twin | ids equal |
  |---|---:|---:|---|
  | `/var/lib/jellyfin/metadata` | 99011 | 99011 | yes |
  | `/var/lib/jellyfin/data` | 196 | 196 | yes |
  | `%MetadataPath%` | 99011 | 99011 | yes |
  | a real movie folder (control) | 447 | 447 | yes |

- The totals moved against the before-table and both differences are accounted for, because
  a difference nobody can explain is not a passing test: the metadata figure is `Person`
  95311 -> 95317 with every other kind unchanged, which is a live server acquiring six
  people; and the data figure was never 193 - that was the count of the `collections`
  subfolder, while `/var/lib/jellyfin/data` also holds `Playlist` 2 and `PlaylistsFolder` 1.
  196 is what the unfixed route already reported for `%AppDataPath%` before any of this.
- The control is untouched at 447 on both routes, and two consecutive reads of the metadata
  path return the same figure, so the numbers are stable rather than merely convenient.

### Changed
- The comment claiming `IncludeItemTypes` kept the two routes identical is gone. It was
  wrong, and it was the reason the defect survived review: the type sets were never the
  problem, both are built from the same `BaseItemKindNames`.

## [11.10.0.1] / [12.10.0.1] - 2026-08-10

### Changed
- `4k` added to the release-evidence pattern, keeping the port level with upstream
  `FileTitleScan` at `f58b8c6`. A title whose only release marker was `4k` would otherwise
  have been exonerated, since the pattern only knew `\d{3,4}p`.
- **Changes nothing on the reference library, and that was measured from both sides before
  it went in.** One entry there matches a search for `4k` and none carries it as a token in a
  name - the `4k` folder appears in paths, not titles. The 837 ids upstream produced *with*
  the change are the same 837 this plugin produced *without* it.
- Verified as a token rather than a substring: `Ocean.14k.Gold.Story` and
  `The.4kids.Dub.Version` are still left alone.

### Verified
- **The two implementations agree row for row, not just in total.** Upstream sent its 837 ids
  and titles; the diff against `FileNameTitleDB` is 0 only-theirs, 0 only-ours, 0 titles
  differing on a shared id - with an invented id as the control that the comparison can fail.
  The previous entry could only claim four matching type sums; this closes the gap it named.

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
