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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Finds the same content held more than once.
/// </summary>
/// <remarks>
/// <para>
/// Every other check in this plugin looks at one folder against itself. These look across
/// folders, which the stock API cannot: the only sound grouping key for episodes,
/// <c>SeriesPresentationUniqueKey</c>, is a column on <c>BaseItemEntity</c> and is absent
/// from <c>BaseItemDto</c>. Grouping by <c>SeriesId</c> instead loses exactly the case that
/// matters, because each folder of a series is its own <c>Series</c> item - and an episode
/// keeps the <c>SeriesId</c> of the folder it lives under even in a user-scoped, merged
/// view.
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
public class DuplicateController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    // Fully qualified: ControllerBase has an instance property called MetadataProvider,
    // which shadows the enum for any unqualified use.
    private static readonly string TmdbName = MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString();
    private static readonly string ImdbName = MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString();

    /// <summary>
    /// Gets every episode file whose number is covered more than once, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned, one row per file.</response>
    /// <returns>The files sharing a season and episode number within one series.</returns>
    [HttpGet("DuplicateEpisode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DuplicateEpisodeDto>> GetDuplicateEpisodes()
    {
        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Episode>();

        var rows = new List<DuplicateEpisodeDto>();
        foreach (var episode in episodes)
        {
            // A null number cannot collide meaningfully - grouping them together would
            // report every seasonless episode as a duplicate of every other.
            if (string.IsNullOrEmpty(episode.Path)
                || episode.IndexNumber is null
                || string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey))
            {
                continue;
            }

            rows.Add(new DuplicateEpisodeDto(
                episode.Id,
                episode.SeriesName,
                episode.SeriesPresentationUniqueKey,
                episode.ParentIndexNumber,
                episode.IndexNumber,
                episode.Name,
                episode.Path,
                episode.Size,
                Pixels(episode.Width),
                Pixels(episode.Height),
                VersionLink(episode.PrimaryVersionId)));
        }

        return Ok(SortEpisodes(KeepColliding(rows)));
    }

    /// <summary>
    /// Gets every episode file whose number is covered more than once, straight from the
    /// database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned, one row per file.</response>
    /// <returns>The files sharing a season and episode number within one series.</returns>
    [HttpGet("DuplicateEpisodeDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DuplicateEpisodeDto>>> GetDuplicateEpisodesFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // One grouped pass for the offending keys, then one query for their rows.
            // Deliberately not a correlated sub-query per row: BaseItems has no index on
            // the columns involved, and that shape cost 15.8 s in SeriesWithoutFilesDB.
            var colliding = await dbContext.BaseItems
                .AsNoTracking()
                .Where(episode => episode.Type == episodeType
                                  && !episode.IsVirtualItem
                                  && episode.Path != null
                                  && episode.IndexNumber != null
                                  && episode.SeriesPresentationUniqueKey != null)
                .GroupBy(episode => new
                {
                    episode.SeriesPresentationUniqueKey,
                    episode.ParentIndexNumber,
                    episode.IndexNumber
                })
                .Where(group => group.Count() > 1)
                .Select(group => new
                {
                    group.Key.SeriesPresentationUniqueKey,
                    group.Key.ParentIndexNumber,
                    group.Key.IndexNumber
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (colliding.Count == 0)
            {
                return Ok(Array.Empty<DuplicateEpisodeDto>());
            }

            // A composite IN is awkward to express, so the rows come back by series key and
            // are matched on the full key here.
            var seriesKeys = colliding.Select(key => key.SeriesPresentationUniqueKey).Distinct().ToList();
            var candidates = await dbContext.BaseItems
                .AsNoTracking()
                .Where(episode => episode.Type == episodeType
                                  && !episode.IsVirtualItem
                                  && episode.Path != null
                                  && episode.IndexNumber != null
                                  && seriesKeys.Contains(episode.SeriesPresentationUniqueKey))
                .Select(episode => new
                {
                    episode.Id,
                    episode.SeriesName,
                    episode.SeriesPresentationUniqueKey,
                    episode.ParentIndexNumber,
                    episode.IndexNumber,
                    episode.Name,
                    episode.Path,
                    episode.Size,
                    episode.Width,
                    episode.Height,
                    episode.PrimaryVersionId
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var wanted = new HashSet<(string? Key, int? Season, int? Episode)>(
                colliding.Select(key => (key.SeriesPresentationUniqueKey, key.ParentIndexNumber, key.IndexNumber)));

            var rows = new List<DuplicateEpisodeDto>();
            foreach (var row in candidates)
            {
                if (!wanted.Contains((row.SeriesPresentationUniqueKey, row.ParentIndexNumber, row.IndexNumber)))
                {
                    continue;
                }

                rows.Add(new DuplicateEpisodeDto(
                    row.Id,
                    row.SeriesName,
                    row.SeriesPresentationUniqueKey,
                    row.ParentIndexNumber,
                    row.IndexNumber,
                    row.Name,
                    row.Path,
                    row.Size,
                    Pixels(row.Width),
                    Pixels(row.Height),
                    VersionLink(row.PrimaryVersionId)));
            }

            return Ok(SortEpisodes(rows));
        }
    }

    /// <summary>
    /// Gets every movie file sharing an identity with another, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned, one row per file.</response>
    /// <returns>The movie files that share a provider id or presentation key.</returns>
    [HttpGet("DuplicateMovie")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DuplicateMovieDto>> GetDuplicateMovies()
    {
        var movies = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Movie>();

        var rows = new List<DuplicateMovieDto>();
        foreach (var movie in movies)
        {
            if (string.IsNullOrEmpty(movie.Path))
            {
                continue;
            }

            var providers = ProviderMap(movie.ProviderIds);
            var key = IdentityKey(
                ProviderValue(providers, TmdbName),
                ProviderValue(providers, ImdbName),
                movie.PresentationUniqueKey);
            if (key is null)
            {
                continue;
            }

            rows.Add(new DuplicateMovieDto(
                movie.Id,
                movie.Name,
                movie.ProductionYear,
                key,
                movie.Path,
                movie.Size,
                Pixels(movie.Width),
                Pixels(movie.Height),
                VersionLink(movie.PrimaryVersionId)));
        }

        return Ok(SortMovies(KeepColliding(rows)));
    }

    /// <summary>
    /// Gets every movie file sharing an identity with another, straight from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned, one row per file.</response>
    /// <returns>The movie files that share a provider id or presentation key.</returns>
    [HttpGet("DuplicateMovieDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DuplicateMovieDto>>> GetDuplicateMoviesFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // A couple of thousand rows, so the grouping happens here rather than in SQL -
            // the identity key is a three-step fallback that SQL would express badly.
            var movies = await dbContext.BaseItems
                .AsNoTracking()
                .Where(movie => movie.Type == movieType
                                && !movie.IsVirtualItem
                                && movie.Path != null)
                .Select(movie => new
                {
                    movie.Id,
                    movie.Name,
                    movie.ProductionYear,
                    movie.Path,
                    movie.Size,
                    movie.Width,
                    movie.Height,
                    movie.PrimaryVersionId,
                    movie.PresentationUniqueKey,
                    Providers = movie.Provider!
                        .Select(provider => new { provider.ProviderId, provider.ProviderValue })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<DuplicateMovieDto>();
            foreach (var movie in movies)
            {
                var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var provider in movie.Providers)
                {
                    if (!string.IsNullOrEmpty(provider.ProviderId) && !string.IsNullOrEmpty(provider.ProviderValue))
                    {
                        providers[provider.ProviderId] = provider.ProviderValue;
                    }
                }

                var key = IdentityKey(
                    ProviderValue(providers, TmdbName),
                    ProviderValue(providers, ImdbName),
                    movie.PresentationUniqueKey);
                if (key is null)
                {
                    continue;
                }

                rows.Add(new DuplicateMovieDto(
                    movie.Id,
                    movie.Name,
                    movie.ProductionYear,
                    key,
                    movie.Path,
                    movie.Size,
                    Pixels(movie.Width),
                    Pixels(movie.Height),
                    VersionLink(movie.PrimaryVersionId)));
            }

            return Ok(SortMovies(KeepColliding(rows)));
        }
    }

    /// <summary>
    /// Picks one provider's value, matching the name the way both routes must: the object
    /// model and the database column need not agree on case, so neither route may depend
    /// on it.
    /// </summary>
    /// <param name="providers">The provider ids of one item.</param>
    /// <param name="name">The provider name to look for.</param>
    /// <returns>The value, or null.</returns>
    private static string? ProviderValue(Dictionary<string, string> providers, string name)
        => providers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>
    /// Rebuilds an item's provider ids into a lookup with a comparer this code controls,
    /// rather than trusting whatever comparer the object model happens to use. The database
    /// side builds the same shape, so both routes match provider names identically.
    /// </summary>
    /// <param name="providerIds">The item's provider ids.</param>
    /// <returns>The same pairs, matched case-insensitively.</returns>
    private static Dictionary<string, string> ProviderMap(Dictionary<string, string>? providerIds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (providerIds is null)
        {
            return map;
        }

        foreach (var pair in providerIds)
        {
            if (!string.IsNullOrEmpty(pair.Key) && !string.IsNullOrEmpty(pair.Value))
            {
                map[pair.Key] = pair.Value;
            }
        }

        return map;
    }

    /// <summary>
    /// Normalises whatever the two Jellyfin lines store for the alternate-version link into
    /// one shape.
    /// </summary>
    /// <remarks>
    /// <c>BaseItemEntity.PrimaryVersionId</c> is a <c>string</c> in 10.11 and a
    /// <c>Guid?</c> in v12 - measured, the net10.0 build refused to compile against the
    /// string form. The response must not change shape with the server line, so both are
    /// reported as the same 32-character string an item id uses elsewhere.
    /// </remarks>
    /// <param name="value">The raw link, of whichever type this line stores.</param>
    /// <returns>The link as a string, or null when there is none.</returns>
    private static string? VersionLink(object? value)
        => value switch
        {
            null => null,
            Guid guid => guid.Equals(Guid.Empty) ? null : guid.ToString("N"),
            string text => string.IsNullOrEmpty(text) ? null : text,
            _ => value.ToString()
        };

    /// <summary>
    /// Builds the identity a movie is grouped on, naming its source so a caller can see
    /// whether the group rests on a provider id or on the weaker fallback.
    /// </summary>
    /// <param name="tmdb">The TMDB id, if any.</param>
    /// <param name="imdb">The IMDB id, if any.</param>
    /// <param name="presentationUniqueKey">Jellyfin's own key, as the last resort.</param>
    /// <returns>The prefixed key, or null when the row cannot be identified at all.</returns>
    private static string? IdentityKey(string? tmdb, string? imdb, string? presentationUniqueKey)
    {
        // Deliberately no name+year fallback: the library holds genuinely distinct films
        // sharing a title, and a false positive costs more here than a miss.
        if (!string.IsNullOrEmpty(tmdb))
        {
            return "Tmdb:" + tmdb;
        }

        if (!string.IsNullOrEmpty(imdb))
        {
            return "Imdb:" + imdb;
        }

        return string.IsNullOrEmpty(presentationUniqueKey) ? null : "Key:" + presentationUniqueKey;
    }

    /// <summary>
    /// Keeps only the rows whose episode number is covered more than once.
    /// </summary>
    /// <param name="rows">Every candidate row.</param>
    /// <returns>The rows belonging to a colliding group.</returns>
    private static List<DuplicateEpisodeDto> KeepColliding(List<DuplicateEpisodeDto> rows)
        => rows
            .GroupBy(row => (row.SeriesKey, row.SeasonNumber, row.EpisodeNumber))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();

    /// <summary>
    /// Keeps only the rows whose identity is shared with another.
    /// </summary>
    /// <param name="rows">Every candidate row.</param>
    /// <returns>The rows belonging to a colliding group.</returns>
    private static List<DuplicateMovieDto> KeepColliding(List<DuplicateMovieDto> rows)
        => rows
            .GroupBy(row => row.IdentityKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();

    /// <summary>
    /// Orders episode findings identically on both routes, so a caller can compare the two
    /// outputs line by line.
    /// </summary>
    /// <param name="rows">The rows to order.</param>
    /// <returns>The rows by series, season, episode and path.</returns>
    private static List<DuplicateEpisodeDto> SortEpisodes(IEnumerable<DuplicateEpisodeDto> rows)
        => rows
            .OrderBy(row => row.SeriesName, StringComparer.Ordinal)
            .ThenBy(row => row.SeasonNumber)
            .ThenBy(row => row.EpisodeNumber)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Orders movie findings identically on both routes.
    /// </summary>
    /// <param name="rows">The rows to order.</param>
    /// <returns>The rows by identity, name and path.</returns>
    private static List<DuplicateMovieDto> SortMovies(IEnumerable<DuplicateMovieDto> rows)
        => rows
            .OrderBy(row => row.IdentityKey, StringComparer.Ordinal)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Normalises a pixel dimension. The column is nullable and the object model is not, so
    /// without this the two routes would report null and 0 for the same unknown value and
    /// the pair would disagree on rows that are in fact identical.
    /// </summary>
    /// <param name="value">The raw width or height.</param>
    /// <returns>The value, or null when it is absent or zero.</returns>
    private static int? Pixels(int? value) => value is null or 0 ? null : value;
}
