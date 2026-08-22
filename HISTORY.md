# History

A narrative log of how this project came to be. The structured, versioned list of
changes lives in [CHANGELOG.md](CHANGELOG.md); this file records the reasoning.

## 2026-07-28 - Why this exists

The sibling tool `jf-lint`
(a separate, private PowerShell tool) has a tab
"Episoden ohne Staffel": episodes whose season Jellyfin could not determine, i.e.
`ParentIndexNumber IS NULL`. In this library that is **6 items out of 25,440** - and
finding them takes **49.7 s** of the tool's ~57 s total runtime. It is by far the
slowest of its six tabs.

The database is not the problem. Counting all 25,440 episodes over HTTP takes 0.22 s.
The ~47 s go into building and serialising 25,440 `BaseItemDto` objects, of which
jf-lint keeps six and throws away 25,434.

**The reason it has to do that: the API cannot express the question.** `/Items` does
have a `parentIndexNumber` filter, but it is an `int?`, and `null` there means "do not
filter" - not "is null":

```csharp
// Jellyfin.Server.Implementations/Item/BaseItemRepository.cs (release-10.11.z)
if (filter.ParentIndexNumber.HasValue)
    baseQuery = baseQuery.Where(e => e.ParentIndexNumber == filter.ParentIndexNumber.Value);
```

Passing a sentinel like `-1` does not help either - it is a plain equality comparison,
so it means "season -1", and in SQL `NULL = -1` is never true. One line further down
sits `ParentIndexNumberNotEquals`, which *does* handle NULL explicitly - but it lives
only in `InternalItemsQuery` and is not reachable over HTTP (confirmed against the
running server's OpenAPI document: `/Items` has 86 parameters, none of them this one).

So the fix has to come from the server side. Jellyfin explicitly supports plugins
contributing API controllers (`AddJellyfinApi` receives the plugin assemblies and
registers them as MVC application parts), which makes a small plugin the direct route.

### Design decisions worth remembering

- **Two endpoints, not one.** `EpisodesWithoutSeason` goes through `ILibraryManager`
  (a promised, stable interface); `EpisodesWithoutSeasonDB` queries `JellyfinDbContext`
  directly (fast, but not a promised plugin contract). They were built together on
  purpose: the point is not only to measure which is faster, but to have each one
  check the other. If the two return different sets, the database query is wrong -
  and that shows up immediately instead of months later.
- **Nothing had to be guessed.** Two open questions from the handover dissolved on
  inspection: `IDbContextFactory<JellyfinDbContext>` *is* registered
  (`AddPooledDbContextFactory<JellyfinDbContext>` in
  `Jellyfin.Server.Implementations/Extensions/ServiceCollectionExtensions.cs`), and the
  `Type` string for episodes does not need hardcoding - `IItemTypeLookup` exposes
  `BaseItemKindNames[BaseItemKind.Episode]`, which is what Jellyfin itself uses.
- **`Policies` comes from `MediaBrowser.Common`, not `Jellyfin.Api`.** The handover's
  controller sketch would not have compiled: `Jellyfin.Api` is not published on NuGet.
  The constants live in `MediaBrowser.Common/Api/Policies.cs`, i.e. in the
  `Jellyfin.Common` package, and are reachable from a plugin. `RequiresElevation` is
  used because the response contains file paths.
- **Multi-targeting is nearly free here.** Jellyfin 10.11 is `net9.0`, v12 is `net10.0`,
  so `<TargetFrameworks>` does the job without any custom MSBuild machinery - and the
  API this plugin touches (`ILibraryManager.GetItemList`, the `InternalItemsQuery`
  fields, `IItemTypeLookup`, `Policies`) is identical in both branches. The v12 build
  is compiled but **untested**, since no v12 server exists here yet.
- **`net9.0` is not a preference, it is a constraint.** A `net10.0` assembly cannot be
  loaded into the .NET 9 runtime that Jellyfin 10.11 runs on. No .NET 9 SDK is needed
  to build it, though - the reference packs are restored from NuGet (verified with a
  full probe build of the real controller code before the project was created).

## 2026-07-29 - Three broken checks in a row

Worth recording, because the pattern repeated: every one of these failures was a *check*
that appeared to pass while testing nothing.

- The repository manifest came out as `[[{…}]]` instead of `[{…}]`. The check for it
  tested `StartsWith('[')` - which `[[` passes. Jellyfin swallowed the resulting
  `JsonException` and the plugin simply never appeared in the catalogue. Found only in the
  server log.
- The verification that fetched the published manifest wrapped it in `@(...)`, which turns
  the malformed bare object into a one-element array. The check normalised away the very
  defect it existed to find.
- "The compiler builds deterministically" came from building twice in a row and comparing
  hashes. Nothing had changed, so MSBuild skipped compilation and never touched the file.
  The retry was no better: the commit meant to sit between the two builds was rejected by
  the changelog hook, so both ran on the same HEAD. Only the third attempt asserted that
  HEAD had actually moved before comparing - and that one uncovered two real sources of
  drift, the commit hash in `AssemblyInformationalVersion` and SourceLink's map in the PDB.

The common thread: a check that touches the data before judging it, or that cannot fail
for the reason it was written. Assert on the raw form, and make the check prove its own
preconditions first.

### Deliberately not done here

Rewriting jf-lint's `$scanEpisodesScript` is a separate step in the other repository,
and it must keep the current code path as a fallback for servers without this plugin.
A pull request against Jellyfin itself - adding a `hasParentIndexNumber` filter, for
which `HasOfficialRating` is an exact template - is the proper general fix, but it
would land in 12.1 at the earliest and so solves nothing today.

## 2026-08-22 - The pair that stopped being a pair

Every query route here exists twice, `X` and `XDB`, for one reason: each is the other's
cross-check. A sibling session reported that the two disagreed - `ItemsByPathDB` returned
0 for `/var/lib/jellyfin/metadata` where `ItemsByPath` returned 99,005 - and relayed a
proposed fix: restrict the library twin's `IncludeItemTypes` to the ten kinds that carry a
path, since the divergence "must be" By-Name-Items whose path is synthesised in memory and
`NULL` in the column.

That explanation was wrong, and so was the fix. It is worth recording why, because the
wrong version was convincing.

**Three claims collapsed in order.** The type sets were never the problem: both halves
derive them from the same `BaseItemKindNames`, so restricting one half would have made
them unequal for the first time. Nor could any list of kinds work - `BaseItemKind.Folder`
sits on *both* sides, 99 column-backed folders under a movie path and one that is not,
so any such list either discards real rows or leaves the contradiction. And the column was
never `NULL`: it holds `%MetadataPath%/Studio/2.0 Entertainment`.

Jellyfin substitutes placeholders for the metadata and data directories on write and
resolves them on read. The library twin saw the resolved form, the database twin compared
against the stored one. **Nothing was missing** - asking the unfixed route for
`%MetadataPath%` returned the same 99,005 the twin reported for the expanded path. Two
numbers matching to the digit is what turned a hypothesis into a diagnosis.

The fix is the one Jellyfin applies to its own path filter: put the caller's path into
stored form before comparing, expand what comes back.

### What the near-miss was

Every proposed remedy up to that point - ours included - narrowed what the routes report.
Each would have made the two numbers agree, and each would have done it by hiding rows
rather than by finding them. A cross-check that is made to agree by removing what it
disagrees about is worse than no cross-check, because it still looks like one.

The habit that caught it was cheap and mechanical: measure the object the other party
named, not one that approximately resembles it, and count on the raw response text -
`@(ConvertFrom-Json '[]')` reports `1`, which had already made one probe look like a hit.
