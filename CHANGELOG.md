# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

### Changed
- Version numbering now encodes the Jellyfin line in the major version: **`11.x.x.x`
  for Jellyfin 10.11**, **`12.x.x.x` for Jellyfin 12** (was `1.0.0` for both). Two
  entries sharing a version would be decided by array order alone after the `targetAbi`
  filter, and a server upgrading from 10.11 to 12 would never be offered the matching
  build. The version is declared per target framework in the project file; `build.ps1`
  reads it from there, so there is one source of truth.
- ZIP file names drop the ABI suffix - the version already identifies the line:
  `jellyfin-plugin-jflint_11.1.0.0.zip`.
