using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
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
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, which
/// owns the placeholder substitution these two routes have to agree on.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ItemLookupController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets every item at a path or beneath it, via <see cref="ILibraryManager"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The safe twin. <c>InternalItemsQuery.Path</c> exists but is an equality filter, so
    /// it cannot serve the "beneath" half; this route therefore materialises the library
    /// and filters in memory, which is slow. It is the insurance against a database schema
    /// change, not the route to reach for.
    /// </para>
    /// <para>
    /// Accepts either spelling of a directory - the real one, or Jellyfin's stored
    /// <c>%MetadataPath%</c> / <c>%AppDataPath%</c> placeholder form - and answers the same
    /// for both, as does its twin.
    /// </para>
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
        var target = ExpandedTarget(path);
        if (target is null)
        {
            return BadRequest("path must be a non-empty path below the library root.");
        }

        var prefix = target + SeparatorOf(target);

        // IncludeItemTypes is not a narrowing of scope but a requirement. A library can
        // hold rows whose Type no longer resolves to a class - leftovers from a plugin
        // that was removed - and an unrestricted GetItemList dies on the first one with
        // "Cannot deserialize unknown type". Naming every kind the server can name keeps
        // those rows out of the deserializer.
        //
        // It does NOT by itself make this route agree with its database twin, and the
        // comment that used to claim it did was wrong: the two halves already drew the
        // same rows, they just spelled Path differently. See the twin below.
        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = itemTypeLookup.BaseItemKindNames.Keys.ToArray(),
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
    /// <para>
    /// The caller's path is put into Jellyfin's <b>stored</b> form before it is compared -
    /// the metadata and data directories are held in the column as <c>%MetadataPath%</c> and
    /// <c>%AppDataPath%</c>, not as real paths - and every returned <c>Path</c> is expanded
    /// back. Without that, this route answered 0 for anything below those two directories
    /// while its twin answered in full.
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
        var target = ExpandedTarget(path);
        if (target is null)
        {
            return BadRequest("path must be a non-empty path below the library root.");
        }

        // Jellyfin does not store the path it hands out. On write it swaps the metadata and
        // data directories for the placeholders %MetadataPath% and %AppDataPath%
        // (BaseItemRepository.GetPathToSave -> IServerApplicationHost.ReverseVirtualPath) and
        // swaps them back when it materialises the item. So the twin above filters expanded
        // paths while this route compares against the stored form, and comparing a caller's
        // real path to the column silently misses everything below those two directories.
        //
        // Measured before this line existed: /var/lib/jellyfin/metadata returned 0 here and
        // 99005 from the twin - and asking this same route for "%MetadataPath%" returned the
        // very same 99005. Nothing was missing; only the spelling differed.
        //
        // Jellyfin filters by path exactly this way (BaseItemRepository: pathToQuery =
        // GetPathToSave(filter.Path)), so matching its behaviour is also what keeps us
        // consistent with whatever it wrote - including the fact that the replacement is
        // unanchored and would rewrite a media path that merely contains one of the two
        // directories as a substring.
        var stored = appHost.ReverseVirtualPath(target);

        // The separator comes from the expanded path: the stored form may be nothing but the
        // placeholder, which carries no separator to read.
        var separator = SeparatorOf(target);

        // The half-open range [stored + separator, stored + (separator + 1)) is exactly
        // "starts with stored + separator". Anchoring on the separator is what keeps
        // /Movies/Ring from swallowing /Movies/Ring2.
        var lower = stored + separator;
        var upper = stored + (char)(separator + 1);

        var shortNames = ShortTypeNames();

        // Same restriction as the twin, for the same reason: a row whose type the server
        // cannot name is one the twin can never return, and a pair that disagrees is worse
        // than a pair that reports a little less.
        var knownTypes = shortNames.Keys.ToArray();

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
                               && knownTypes.Contains(item.Type)
                               && (item.Path == stored
                                   || (item.Path.CompareTo(lower) >= 0
                                       && item.Path.CompareTo(upper) < 0)))
                .Select(item => new { item.Id, item.Type, item.Name, item.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Report the expanded form, the same one the twin reports. Without this the two
            // routes would return the same items under different Path strings, and Sorted()
            // would even order them differently.
            var found = rows.Select(row => new PathItemDto(
                row.Id,
                shortNames[row.Type],
                row.Name,
                row.Path is null ? null : appHost.ExpandVirtualPath(row.Path)));

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
    /// Brings a caller-supplied path into the one form both routes start from: trailing
    /// separator gone, and the <c>%MetadataPath%</c> / <c>%AppDataPath%</c> placeholders
    /// resolved to real directories. Both routes call this, which is what lets a caller hand
    /// in either spelling of the same directory and get the same answer from either one.
    /// </summary>
    /// <param name="path">The path as supplied by the caller.</param>
    /// <returns>The comparable path, or null when the input cannot be used.</returns>
    private string? ExpandedTarget(string? path)
    {
        var trimmed = Normalize(path);
        if (trimmed is null)
        {
            return null;
        }

        // Normalized a second time on purpose: the expansion pastes in a directory that
        // belongs to Jellyfin's configuration, not to us, and a trailing separator in it
        // would otherwise reach the comparison.
        return Normalize(appHost.ExpandVirtualPath(trimmed));
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
