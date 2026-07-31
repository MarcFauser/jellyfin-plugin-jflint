using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Looks items up by their path on disk.
/// </summary>
/// <remarks>
/// <para>
/// The odd one out in this plugin: every other route is a whole-library finding, this one
/// takes a parameter and answers "what is <em>here</em>". It exists because the stock API
/// treats <c>Path</c> as an output field only, so a caller holding a path and needing an
/// item id - which is all <c>/Items/{id}/Refresh</c> and <c>DELETE /Items/{id}</c> accept -
/// has to read the entire item list and match locally. Measured at ~7 s and ~50,000 rows
/// to identify one item.
/// </para>
/// <para>
/// A name search does not close that gap, because the case where an id is needed most is
/// the case where the name is wrong: a folder called <c>Ring.2002…</c> held under the name
/// of an entirely different film is exactly when a refresh is wanted, and exactly when no
/// name-based search can find it.
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
public class ItemLookupController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets every item at a path or beneath it, via <see cref="ILibraryManager"/>.
    /// </summary>
    /// <remarks>
    /// The safe twin. <c>InternalItemsQuery.Path</c> exists but is an equality filter, so
    /// it cannot serve the "beneath" half; this route therefore materialises the library
    /// and filters in memory, which is slow. It is the insurance against a database schema
    /// change, not the route to reach for.
    /// </remarks>
    /// <param name="path">The file or folder path to look up.</param>
    /// <response code="200">Items returned; an empty array when the path holds nothing.</response>
    /// <response code="400">The path was empty, or was a bare root.</response>
    /// <returns>The item at that path plus everything beneath it.</returns>
    [HttpGet("ItemsByPath")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<PathItemDto>> GetItemsByPath([FromQuery] string? path)
    {
        var target = Normalize(path);
        if (target is null)
        {
            return BadRequest("path must be a non-empty path below the library root.");
        }

        var prefix = target + SeparatorOf(target);

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false
        });

        var found = items
            .Where(item => item.Path is not null
                           && (string.Equals(item.Path, target, StringComparison.Ordinal)
                               || item.Path.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(item => new PathItemDto(
                item.Id,
                item.GetBaseItemKind().ToString(),
                item.Name,
                item.Path));

        return Ok(Sorted(found));
    }

    /// <summary>
    /// Gets every item at a path or beneath it, straight from the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is <b>ordinal and case-sensitive</b>, matching SQLite's default
    /// BINARY collation and the case-sensitive file systems Jellyfin usually runs on. A
    /// caller comparing case-insensitively will disagree with this route on a system where
    /// that matters.
    /// </para>
    /// <para>
    /// Note the half-open range rather than the obvious <c>StartsWith</c>: EF turns
    /// <c>StartsWith</c> into <c>LIKE … ESCAPE '\'</c>, and SQLite does not use an index
    /// for a LIKE carrying an ESCAPE clause - which would have thrown away the one index
    /// this route depends on. Measured from the generated SQL, not assumed.
    /// </para>
    /// </remarks>
    /// <param name="path">The file or folder path to look up.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Items returned; an empty array when the path holds nothing.</response>
    /// <response code="400">The path was empty, or was a bare root.</response>
    /// <returns>The item at that path plus everything beneath it.</returns>
    [HttpGet("ItemsByPathDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<PathItemDto>>> GetItemsByPathFromDatabaseAsync(
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        var target = Normalize(path);
        if (target is null)
        {
            return BadRequest("path must be a non-empty path below the library root.");
        }

        var separator = SeparatorOf(target);

        // The half-open range [target + separator, target + (separator + 1)) is exactly
        // "starts with target + separator". Anchoring on the separator is what keeps
        // /Movies/Ring from swallowing /Movies/Ring2.
        var lower = target + separator;
        var upper = target + (char)(separator + 1);

        var shortNames = ShortTypeNames();

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // CompareTo is culture-sensitive in C#, but it is never executed here: EF
            // translates it to a plain SQL >= / <, which uses the column's BINARY collation
            // and is therefore ordinal. EF Core throws rather than evaluating on the client,
            // so the C# semantics cannot leak in. The ordinal-explicit forms are not an
            // option - measured, both string.Compare(a, b, StringComparison.Ordinal) and
            // string.CompareOrdinal fail to translate at all.
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(item => !item.IsVirtualItem
                               && item.Path != null
                               && (item.Path == target
                                   || (item.Path.CompareTo(lower) >= 0
                                       && item.Path.CompareTo(upper) < 0)))
                .Select(item => new { item.Id, item.Type, item.Name, item.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var found = rows.Select(row => new PathItemDto(
                row.Id,
                shortNames.TryGetValue(row.Type, out var shortName) ? shortName : row.Type,
                row.Name,
                row.Path));

            return Ok(Sorted(found));
        }
    }

    /// <summary>
    /// Trims a trailing separator and refuses anything that leaves nothing behind. A bare
    /// root would match the whole library, which is never what a lookup means.
    /// </summary>
    /// <param name="path">The path as supplied by the caller.</param>
    /// <returns>The comparable path, or null when the input cannot be used.</returns>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().TrimEnd('/', '\\');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Picks the path separator the given path uses, so the route also holds on a Jellyfin
    /// running on Windows.
    /// </summary>
    /// <param name="path">The normalized path.</param>
    /// <returns>The separator character.</returns>
    private static char SeparatorOf(string path)
        => path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal)
            ? '\\'
            : '/';

    /// <summary>
    /// Orders findings the same way on both routes, so a caller can compare them line by
    /// line.
    /// </summary>
    /// <param name="items">The items to order.</param>
    /// <returns>The items by path, then id.</returns>
    private static List<PathItemDto> Sorted(IEnumerable<PathItemDto> items)
        => items
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();

    /// <summary>
    /// Inverts <see cref="IItemTypeLookup.BaseItemKindNames"/> so a stored fully qualified
    /// type name can be reported as its short name. Built from the same lookup Jellyfin
    /// uses, never by splitting the type string.
    /// </summary>
    /// <returns>Fully qualified type name to short name.</returns>
    private Dictionary<string, string> ShortTypeNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in itemTypeLookup.BaseItemKindNames)
        {
            names[pair.Value] = pair.Key.ToString();
        }

        return names;
    }
}
