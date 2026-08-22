using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
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
/// Library-lint queries that the stock Jellyfin API cannot express.
/// </summary>
/// <remarks>
/// Requires elevation because the responses contain media file paths.
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used
/// to expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class JFLintController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets every episode whose season could not be determined, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <remarks>
    /// Uses only public, promised interfaces. It still materialises every episode in
    /// the library before filtering, so it is the slower of the two routes - but it
    /// survives database schema changes. Kept as the cross-check for, and fallback
    /// from, <see cref="GetEpisodesWithoutSeasonFromDatabaseAsync"/>.
    /// </remarks>
    /// <response code="200">Episodes without a season returned.</response>
    /// <returns>The episodes whose <c>ParentIndexNumber</c> is not set.</returns>
    [HttpGet("EpisodesWithoutSeason")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<OrphanEpisodeDto>> GetEpisodesWithoutSeason()
    {
        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        });

        var orphans = items.OfType<Episode>()
            .Where(episode => episode.ParentIndexNumber is null)
            .Select(episode => new OrphanEpisodeDto(
                episode.Id,
                SeriesIdOf(episode.SeriesId),
                episode.SeriesName,
                episode.IndexNumber,
                episode.Name,
                episode.Path));

        return Ok(Sorted(orphans));
    }

    /// <summary>
    /// Gets every episode whose season could not be determined, straight from the
    /// database.
    /// </summary>
    /// <remarks>
    /// The filter runs as SQL, so only the matching rows leave the database. Faster,
    /// but <c>JellyfinDbContext</c> is not a promised plugin contract: its schema may
    /// change between major Jellyfin versions, which is why the plugin is built per
    /// version and why the <see cref="GetEpisodesWithoutSeason"/> route stays.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Episodes without a season returned.</response>
    /// <returns>The episodes whose <c>ParentIndexNumber</c> is not set.</returns>
    [HttpGet("EpisodesWithoutSeasonDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrphanEpisodeDto>>> GetEpisodesWithoutSeasonFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        // Jellyfin stores the fully qualified type name in BaseItemEntity.Type. Ask the
        // same lookup the server uses instead of hardcoding the string.
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(item => item.Type == episodeType
                               && item.ParentIndexNumber == null
                               && !item.IsVirtualItem)
                .Select(item => new { item.Id, item.SeriesId, item.SeriesName, item.IndexNumber, item.Name, item.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Materialised first, because the two conversions below are plain C#: the path
            // has to be expanded out of its stored form, and neither could run inside the
            // SQL translation.
            var orphans = rows.Select(row => new OrphanEpisodeDto(
                row.Id,
                SeriesIdOf(row.SeriesId),
                row.SeriesName,
                row.IndexNumber,
                row.Name,
                StoredPath.Expand(appHost, row.Path)));

            return Ok(Sorted(orphans));
        }
    }

    /// <summary>
    /// Reports an unset series link as null on both routes. The column is nullable, but the
    /// write path stores <see cref="Guid.Empty"/> rather than NULL, and materialising turns a
    /// NULL into <see cref="Guid.Empty"/> as well - so without this the two halves could
    /// report the same missing link as <c>null</c> and as an all-zero guid.
    /// </summary>
    /// <param name="value">The series id as either half holds it.</param>
    /// <returns>The series id, or null when there is none.</returns>
    private static Guid? SeriesIdOf(Guid? value)
        => value is null || value == Guid.Empty ? null : value;

    /// <summary>
    /// Orders findings the same way on both routes.
    /// </summary>
    /// <remarks>
    /// This pair was the only one in the plugin where neither half imposed an order. That is
    /// not the same as both being unordered: the library half inherits Jellyfin's default
    /// <c>OrderBy(SortName)</c> from <c>BaseItemRepository.ApplyOrder</c>, while the database
    /// half had no <c>OrderBy</c> at all and got SQLite's scan order. The two therefore could
    /// not be compared element by element, which is the only reason the pair exists. It went
    /// unnoticed because the route returns nothing on the reference library.
    /// </remarks>
    /// <param name="items">The items to order.</param>
    /// <returns>The items in a deterministic order.</returns>
    private static List<OrphanEpisodeDto> Sorted(IEnumerable<OrphanEpisodeDto> items)
        => items
            .OrderBy(item => item.SeriesName, StringComparer.Ordinal)
            .ThenBy(item => item.IndexNumber)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();
}
