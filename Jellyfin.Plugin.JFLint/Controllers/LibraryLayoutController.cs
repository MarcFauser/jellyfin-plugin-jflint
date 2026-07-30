using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Findings about how the library is laid out on disk: season folders Jellyfin could not
/// read a number from, folders that yielded no video, duplicate season numbers, series
/// whose files it read none of, and rows pointing at a parent that no longer exists.
/// </summary>
/// <remarks>
/// <para>
/// Every finding gets two routes - one over <see cref="ILibraryManager"/>, one straight
/// off the database with a <c>DB</c> suffix. The pair is deliberate redundancy: the
/// database route is fast but <c>JellyfinDbContext</c> is not a promised plugin contract,
/// and the two are each other's test, since both must return the same set item by item.
/// </para>
/// <para>
/// Requires elevation because the responses contain media file paths.
/// </para>
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class LibraryLayoutController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    // The short names the responses carry, tied to the enum rather than written out, so a
    // rename upstream breaks the build instead of the payload.
    private const string SeriesTypeName = nameof(BaseItemKind.Series);
    private const string SeasonTypeName = nameof(BaseItemKind.Season);
    private const string EpisodeTypeName = nameof(BaseItemKind.Episode);

    // Likewise for the link names reported by OrphanedItem: taken from the columns they
    // name, not from string literals.
    private const string SeriesLink = nameof(BaseItemEntity.SeriesId);
    private const string SeasonLink = nameof(BaseItemEntity.SeasonId);
    private const string ParentLink = nameof(BaseItemEntity.ParentId);

    /// <summary>
    /// Gets season folders without a readable number that do hold files, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>The seasons Jellyfin could not read a number from.</returns>
    [HttpGet("PhantomSeason")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LayoutFindingDto>> GetPhantomSeasons()
        => Ok(FindingsOfKind(LayoutFindingKind.PhantomSeason));

    /// <summary>
    /// Gets season folders holding no playable episode, via <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>The season folders without a video file.</returns>
    [HttpGet("SeasonFolderWithoutVideo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LayoutFindingDto>> GetSeasonFoldersWithoutVideo()
        => Ok(FindingsOfKind(LayoutFindingKind.SeasonFolderWithoutVideo));

    /// <summary>
    /// Gets seasons sharing a number within the same series, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>Every member of every group of seasons that share a number.</returns>
    [HttpGet("DuplicateSeasonNumber")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LayoutFindingDto>> GetDuplicateSeasonNumbers()
        => Ok(FindingsOfKind(LayoutFindingKind.DuplicateSeasonNumber));

    /// <summary>
    /// Gets series folders that yielded no playable episode, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>The series without a single playable episode.</returns>
    [HttpGet("SeriesWithoutFiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LayoutFindingDto>> GetSeriesWithoutFiles()
        => Ok(FindingsOfKind(LayoutFindingKind.SeriesWithoutFiles));

    /// <summary>
    /// Gets seasons and episodes pointing at a row that no longer exists, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>The items with a dangling series, season or parent link.</returns>
    [HttpGet("OrphanedItem")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LayoutFindingDto>> GetOrphanedItems()
        => Ok(FindingsOfKind(LayoutFindingKind.OrphanedItem));

    /// <summary>
    /// Gets season folders without a readable number that do hold files, straight from the
    /// database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>The seasons Jellyfin could not read a number from.</returns>
    [HttpGet("PhantomSeasonDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LayoutFindingDto>>> GetPhantomSeasonsFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var seasonType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Season];
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(season => season.Type == seasonType
                                 && !string.IsNullOrEmpty(season.Path)
                                 && season.IndexNumber == null
                                 && dbContext.BaseItems.Any(episode => episode.Type == episodeType
                                                                       && !episode.IsVirtualItem
                                                                       && episode.ParentId == season.Id))
                .Select(season => new { season.Id, season.Name, season.SeriesName, season.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Ok(Sorted(rows.Select(row => new LayoutFindingDto
            {
                Kind = LayoutFindingKind.PhantomSeason,
                ItemId = row.Id,
                ItemType = SeasonTypeName,
                Name = row.Name,
                SeriesName = row.SeriesName,
                Path = row.Path
            })));
        }
    }

    /// <summary>
    /// Gets season folders holding no playable episode, straight from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>The season folders without a video file.</returns>
    [HttpGet("SeasonFolderWithoutVideoDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LayoutFindingDto>>> GetSeasonFoldersWithoutVideoFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var seasonType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Season];
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(season => season.Type == seasonType
                                 && !string.IsNullOrEmpty(season.Path)
                                 && !dbContext.BaseItems.Any(episode => episode.Type == episodeType
                                                                        && !episode.IsVirtualItem
                                                                        && episode.ParentId == season.Id))
                .Select(season => new { season.Id, season.Name, season.SeriesName, season.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Ok(Sorted(rows.Select(row => new LayoutFindingDto
            {
                Kind = LayoutFindingKind.SeasonFolderWithoutVideo,
                ItemId = row.Id,
                ItemType = SeasonTypeName,
                Name = row.Name,
                SeriesName = row.SeriesName,
                Path = row.Path
            })));
        }
    }

    /// <summary>
    /// Gets seasons sharing a number within the same series, straight from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>Every member of every group of seasons that share a number.</returns>
    [HttpGet("DuplicateSeasonNumberDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LayoutFindingDto>>> GetDuplicateSeasonNumbersFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var seasonType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Season];

        // A local, so EF parameterises it instead of choking on the static field.
        var unset = Guid.Empty;

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // GROUP BY ... HAVING COUNT(*) > 1 gives the offending keys; a composite IN is
            // awkward to express, so the rows come back by series and are matched here.
            var duplicateKeys = await dbContext.BaseItems
                .AsNoTracking()
                .Where(season => season.Type == seasonType
                                 && season.IndexNumber != null
                                 && season.SeriesId != null
                                 && season.SeriesId != unset)
                .GroupBy(season => new { season.SeriesId, season.IndexNumber })
                .Where(group => group.Count() > 1)
                .Select(group => new { group.Key.SeriesId, group.Key.IndexNumber, GroupSize = group.Count() })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (duplicateKeys.Count == 0)
            {
                return Ok(Array.Empty<LayoutFindingDto>());
            }

            var affectedSeries = duplicateKeys.Select(key => key.SeriesId).Distinct().ToList();
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(season => season.Type == seasonType
                                 && season.IndexNumber != null
                                 && affectedSeries.Contains(season.SeriesId))
                .Select(season => new
                {
                    season.Id,
                    season.Name,
                    season.SeriesName,
                    season.Path,
                    season.SeriesId,
                    season.IndexNumber
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var groupSizes = duplicateKeys.ToDictionary(key => (key.SeriesId, key.IndexNumber), key => key.GroupSize);

            return Ok(Sorted(rows
                .Where(row => groupSizes.ContainsKey((row.SeriesId, row.IndexNumber)))
                .Select(row => new LayoutFindingDto
                {
                    Kind = LayoutFindingKind.DuplicateSeasonNumber,
                    ItemId = row.Id,
                    ItemType = SeasonTypeName,
                    Name = row.Name,
                    SeriesName = row.SeriesName,
                    Path = row.Path,
                    SeasonNumber = row.IndexNumber,
                    GroupSize = groupSizes[(row.SeriesId, row.IndexNumber)]
                })));
        }
    }

    /// <summary>
    /// Gets series folders that yielded no playable episode, straight from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>The series without a single playable episode.</returns>
    [HttpGet("SeriesWithoutFilesDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LayoutFindingDto>>> GetSeriesWithoutFilesFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var seriesType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Series];
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];
        var unset = Guid.Empty;

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // Deliberately NOT a correlated subquery per series. BaseItems carries its own
            // index on ParentId but none on SeriesId - checked against the EF model - so the
            // obvious "NOT EXISTS (... WHERE SeriesId = series.Id)" costs one table scan per
            // series row. Measured against 1,585 series: 15.8 s, which is slower than the
            // ILibraryManager route this is supposed to replace and therefore pointless. One
            // grouped pass over the episodes answers both questions in a single scan.
            var perSeries = await dbContext.BaseItems
                .AsNoTracking()
                .Where(episode => episode.Type == episodeType
                                  && episode.SeriesId != null
                                  && episode.SeriesId != unset)
                .GroupBy(episode => episode.SeriesId)
                .Select(group => new
                {
                    SeriesId = group.Key,
                    Rows = group.Count(),
                    Playable = group.Sum(episode => episode.IsVirtualItem ? 0 : 1)
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var rowsPerSeries = new Dictionary<Guid, int>();
            var seriesWithVideo = new HashSet<Guid>();
            foreach (var entry in perSeries)
            {
                if (entry.SeriesId is not Guid seriesId)
                {
                    continue;
                }

                rowsPerSeries[seriesId] = entry.Rows;
                if (entry.Playable > 0)
                {
                    seriesWithVideo.Add(seriesId);
                }
            }

            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(series => series.Type == seriesType && !string.IsNullOrEmpty(series.Path))
                .Select(series => new { series.Id, series.Name, series.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var findings = new List<LayoutFindingDto>();
            foreach (var row in rows)
            {
                if (seriesWithVideo.Contains(row.Id))
                {
                    continue;
                }

                rowsPerSeries.TryGetValue(row.Id, out var rowCount);
                findings.Add(new LayoutFindingDto
                {
                    Kind = LayoutFindingKind.SeriesWithoutFiles,
                    ItemId = row.Id,
                    ItemType = SeriesTypeName,
                    Name = row.Name,
                    Path = row.Path,
                    EpisodeRowCount = rowCount
                });
            }

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Gets seasons and episodes pointing at a row that no longer exists, straight from the
    /// database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>The items with a dangling series, season or parent link.</returns>
    [HttpGet("OrphanedItemDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LayoutFindingDto>>> GetOrphanedItemsFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var seasonType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Season];
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];
        var unset = Guid.Empty;

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // SeriesId and SeasonId are written raw from non-nullable Guid sources, so an
            // unset link is Guid.Empty rather than NULL; only ParentId is normalised by the
            // server. Without the Guid.Empty guard every seasonless episode would show up
            // here.
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(item => item.Type == seasonType || item.Type == episodeType)
                .Select(item => new
                {
                    item.Id,
                    item.Type,
                    item.Name,
                    item.SeriesName,
                    item.Path,
                    item.SeriesId,
                    item.SeasonId,
                    item.ParentId,
                    SeriesMissing = item.SeriesId != null
                                    && item.SeriesId != unset
                                    && !dbContext.BaseItems.Any(row => row.Id == item.SeriesId),
                    SeasonMissing = item.SeasonId != null
                                    && item.SeasonId != unset
                                    && !dbContext.BaseItems.Any(row => row.Id == item.SeasonId),
                    ParentMissing = item.ParentId != null
                                    && !dbContext.BaseItems.Any(row => row.Id == item.ParentId)
                })
                .Where(row => row.SeriesMissing || row.SeasonMissing || row.ParentMissing)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Ok(Sorted(rows.Select(row => new LayoutFindingDto
            {
                Kind = LayoutFindingKind.OrphanedItem,
                ItemId = row.Id,
                ItemType = string.Equals(row.Type, seasonType, StringComparison.Ordinal)
                    ? SeasonTypeName
                    : EpisodeTypeName,
                Name = row.Name,
                SeriesName = row.SeriesName,
                Path = row.Path,
                DanglingLink = row.SeriesMissing ? SeriesLink : row.SeasonMissing ? SeasonLink : ParentLink,
                DanglingId = row.SeriesMissing ? row.SeriesId : row.SeasonMissing ? row.SeasonId : row.ParentId
            })));
        }
    }

    /// <summary>
    /// Orders findings the same way on both routes, which is what lets a caller compare the
    /// two outputs line by line.
    /// </summary>
    /// <param name="findings">The findings to order.</param>
    /// <returns>The findings by series name, then season number, then name, nulls last.</returns>
    private static List<LayoutFindingDto> Sorted(IEnumerable<LayoutFindingDto> findings)
        => findings
            .OrderBy(finding => finding.SeriesName is null)
            .ThenBy(finding => finding.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.SeasonNumber is null)
            .ThenBy(finding => finding.SeasonNumber)
            .ThenBy(finding => finding.Name is null)
            .ThenBy(finding => finding.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Builds the findings for one kind. All five routes share
    /// <see cref="BuildFindingsFromLibrary"/>, so a single request materialises the library
    /// once however many kinds it asks about.
    /// </summary>
    /// <param name="kind">The kind to keep.</param>
    /// <returns>The findings of that kind, ordered.</returns>
    private List<LayoutFindingDto> FindingsOfKind(string kind)
        => Sorted(BuildFindingsFromLibrary().Where(finding => string.Equals(finding.Kind, kind, StringComparison.Ordinal)));

    /// <summary>
    /// Computes all five findings from the object model. This is the slow route: it
    /// materialises every series, season and episode. It exists because it only touches
    /// promised interfaces and therefore survives a database schema change - and because
    /// it is the cross-check for the database routes.
    /// </summary>
    /// <returns>Every finding, unordered.</returns>
    private List<LayoutFindingDto> BuildFindingsFromLibrary()
    {
        var allSeries = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        }).OfType<Series>().ToList();

        var allSeasons = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Season],
            Recursive = true
        }).OfType<Season>().ToList();

        // Virtual rows are wanted here - EpisodeRowCount counts them - so they are filtered
        // per finding rather than by the query.
        var allEpisodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).OfType<Episode>().ToList();

        var findings = new List<LayoutFindingDto>();
        AddSeasonFolderFindings(findings, allSeasons, allEpisodes);
        AddDuplicateSeasonNumberFindings(findings, allSeasons);
        AddSeriesWithoutFilesFindings(findings, allSeries, allEpisodes);
        AddOrphanedItemFindings(findings, allSeries, allSeasons, allEpisodes);
        return findings;
    }

    /// <summary>
    /// Adds the two season-folder findings. They are mutually exclusive, which is why they
    /// share a pass: a folder either yielded a playable episode or it did not.
    /// </summary>
    /// <param name="findings">The list to add to.</param>
    /// <param name="seasons">Every season in the library.</param>
    /// <param name="episodes">Every episode row, real and virtual.</param>
    private static void AddSeasonFolderFindings(
        List<LayoutFindingDto> findings,
        List<Season> seasons,
        List<Episode> episodes)
    {
        // ParentId, not SeasonId: the latter is inferred from ParentIndexNumber when the
        // file is not in a season folder, so it can name a season the file does not sit
        // under - which is the very situation PhantomSeason is about.
        var foldersWithVideo = new HashSet<Guid>();
        foreach (var episode in episodes)
        {
            if (!episode.IsVirtualItem)
            {
                foldersWithVideo.Add(episode.ParentId);
            }
        }

        foreach (var season in seasons)
        {
            if (string.IsNullOrEmpty(season.Path))
            {
                continue;
            }

            if (!foldersWithVideo.Contains(season.Id))
            {
                findings.Add(SeasonFinding(LayoutFindingKind.SeasonFolderWithoutVideo, season));
            }
            else if (season.IndexNumber is null)
            {
                findings.Add(SeasonFinding(LayoutFindingKind.PhantomSeason, season));
            }
        }
    }

    /// <summary>
    /// Adds every member of every group of seasons sharing a number within one series.
    /// </summary>
    /// <param name="findings">The list to add to.</param>
    /// <param name="seasons">Every season in the library.</param>
    private static void AddDuplicateSeasonNumberFindings(List<LayoutFindingDto> findings, List<Season> seasons)
    {
        var groups = seasons
            .Where(season => season.IndexNumber is not null && season.SeriesId != Guid.Empty)
            .GroupBy(season => (season.SeriesId, season.IndexNumber));

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            foreach (var season in members)
            {
                findings.Add(new LayoutFindingDto
                {
                    Kind = LayoutFindingKind.DuplicateSeasonNumber,
                    ItemId = season.Id,
                    ItemType = SeasonTypeName,
                    Name = season.Name,
                    SeriesName = season.SeriesName,
                    Path = season.Path,
                    SeasonNumber = season.IndexNumber,
                    GroupSize = members.Count
                });
            }
        }
    }

    /// <summary>
    /// Adds the series folders that yielded no playable episode.
    /// </summary>
    /// <param name="findings">The list to add to.</param>
    /// <param name="allSeries">Every series in the library.</param>
    /// <param name="episodes">Every episode row, real and virtual.</param>
    private static void AddSeriesWithoutFilesFindings(
        List<LayoutFindingDto> findings,
        List<Series> allSeries,
        List<Episode> episodes)
    {
        var seriesWithVideo = new HashSet<Guid>();
        var rowsPerSeries = new Dictionary<Guid, int>();
        foreach (var episode in episodes)
        {
            var seriesId = episode.SeriesId;
            if (seriesId == Guid.Empty)
            {
                continue;
            }

            rowsPerSeries.TryGetValue(seriesId, out var seen);
            rowsPerSeries[seriesId] = seen + 1;

            if (!episode.IsVirtualItem)
            {
                seriesWithVideo.Add(seriesId);
            }
        }

        foreach (var series in allSeries)
        {
            if (string.IsNullOrEmpty(series.Path) || seriesWithVideo.Contains(series.Id))
            {
                continue;
            }

            rowsPerSeries.TryGetValue(series.Id, out var rowCount);
            findings.Add(new LayoutFindingDto
            {
                Kind = LayoutFindingKind.SeriesWithoutFiles,
                ItemId = series.Id,
                ItemType = SeriesTypeName,
                Name = series.Name,
                Path = series.Path,
                EpisodeRowCount = rowCount
            });
        }
    }

    /// <summary>
    /// Adds the seasons and episodes whose series, season or parent link names a row that
    /// is gone.
    /// </summary>
    /// <param name="findings">The list to add to.</param>
    /// <param name="allSeries">Every series in the library.</param>
    /// <param name="seasons">Every season in the library.</param>
    /// <param name="episodes">Every episode row, real and virtual.</param>
    private void AddOrphanedItemFindings(
        List<LayoutFindingDto> findings,
        List<Series> allSeries,
        List<Season> seasons,
        List<Episode> episodes)
    {
        var knownIds = new HashSet<Guid>();
        foreach (var series in allSeries)
        {
            knownIds.Add(series.Id);
        }

        foreach (var season in seasons)
        {
            knownIds.Add(season.Id);
        }

        foreach (var episode in episodes)
        {
            knownIds.Add(episode.Id);
        }

        var checkedIds = new Dictionary<Guid, bool>();

        // A link may legitimately point outside those three lists - at a library folder, for
        // instance - so anything not found among them is confirmed against the library
        // before it is reported. Only the suspicious few reach that lookup.
        bool IsGone(Guid id)
        {
            if (id == Guid.Empty || knownIds.Contains(id))
            {
                return false;
            }

            if (checkedIds.TryGetValue(id, out var gone))
            {
                return gone;
            }

            gone = libraryManager.GetItemById(id) is null;
            checkedIds[id] = gone;
            return gone;
        }

        // One finding per item, not per link: a series that vanished leaves both SeriesId
        // and ParentId dangling on the same row, and reporting it twice would double a
        // count the caller shows as "how many items are broken".
        void AddIfDangling(BaseItem item, string itemType, string? seriesName, Guid seriesId, Guid seasonId)
        {
            string? link = null;
            var danglingId = Guid.Empty;

            if (IsGone(seriesId))
            {
                link = SeriesLink;
                danglingId = seriesId;
            }
            else if (IsGone(seasonId))
            {
                link = SeasonLink;
                danglingId = seasonId;
            }
            else if (IsGone(item.ParentId))
            {
                link = ParentLink;
                danglingId = item.ParentId;
            }

            if (link is null)
            {
                return;
            }

            findings.Add(new LayoutFindingDto
            {
                Kind = LayoutFindingKind.OrphanedItem,
                ItemId = item.Id,
                ItemType = itemType,
                Name = item.Name,
                SeriesName = seriesName,
                Path = item.Path,
                DanglingLink = link,
                DanglingId = danglingId
            });
        }

        foreach (var season in seasons)
        {
            AddIfDangling(season, SeasonTypeName, season.SeriesName, season.SeriesId, Guid.Empty);
        }

        foreach (var episode in episodes)
        {
            AddIfDangling(episode, EpisodeTypeName, episode.SeriesName, episode.SeriesId, episode.SeasonId);
        }
    }

    /// <summary>
    /// Builds a finding about a season, which the two season-folder kinds share.
    /// </summary>
    /// <param name="kind">The finding kind.</param>
    /// <param name="season">The season the finding is about.</param>
    /// <returns>The finding.</returns>
    private static LayoutFindingDto SeasonFinding(string kind, Season season)
        => new()
        {
            Kind = kind,
            ItemId = season.Id,
            ItemType = SeasonTypeName,
            Name = season.Name,
            SeriesName = season.SeriesName,
            Path = season.Path
        };
}
