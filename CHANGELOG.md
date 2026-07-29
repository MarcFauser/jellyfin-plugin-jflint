# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

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
