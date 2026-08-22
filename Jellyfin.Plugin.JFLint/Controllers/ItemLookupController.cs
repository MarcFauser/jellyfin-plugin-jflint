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
    /// The placeholders Jellyfin substitutes into the <c>Path</c> column.
    /// </summary>
    /// <remarks>
    /// This is a hand-written list, and calling it anything else would be dishonest - but note
    /// what it lists: two <em>spellings of directories</em>, not item kinds. It says nothing
    /// about what may be found, and it is the reason no list of kinds is needed anywhere.
    /// <para>
    /// It cannot be derived. <see cref="IServerApplicationHost"/> exposes the two conversions
    /// but not the table behind them, and <c>IServerApplicationPaths</c> - which does name
    /// both placeholders - is never registered with the container: <c>ApplicationHost</c>
    /// registers that instance as <c>IApplicationPaths</c> alone, so asking for it would fail
    /// at runtime. Checked, with the registration of <see cref="IServerApplicationHost"/> as
    /// the positive control, since that is the one this controller already relies on.
    /// </para>
    /// <para>
    /// The entries are self-checking: a placeholder this server does not substitute comes back
    /// from <c>ExpandVirtualPath</c> unchanged and is dropped by <see cref="StoredRoots"/>, so
    /// a stale entry costs nothing and a missing one is the only real risk.
    /// </para>
    /// </remarks>
    private static readonly string[] VirtualPathTokens = ["%AppDataPath%", "%MetadataPath%"];

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

        var shortNames = ShortTypeNames();

        var found = items
            .Where(item => item.Path is not null
                           && (string.Equals(item.Path, target, StringComparison.Ordinal)
                               || item.Path.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(item => new PathItemDto(
                item.Id,
                ShortTypeNameOf(item, shortNames),
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
        // Reversing the caller's path is necessary and NOT sufficient, which cost this route
        // a second release: ReverseVirtualPath is an unanchored Replace, so it rewrites a
        // target that lies at or below one of those directories and leaves an ANCESTOR of
        // them untouched. Asking for /var/lib/jellyfin therefore built a range around the
        // real path, and no range around a real path can reach a column value that begins
        // "%MetadataPath%". Measured with only this line in place: 10 rows here against
        // 99220 from the twin - a well-formed, plausible, wrong answer, which is worse than
        // the obvious 0 it replaced.
        //
        // Nor can one range be widened to cover it. Under the column's BINARY collation the
        // rows below such an ancestor sit in disjoint stretches with the media library
        // sorting between them - measured: "%AppDataPath%/..." < "%MetadataPath%/..." <
        // the media root < "/var/lib/jellyfin/root/...". A single half-open range spanning
        // the outermost two would swallow the entire library.
        //
        // So: one range per stored root, concatenated. Verified against the live server at id
        // level - the ranges do not overlap, and their union is exactly the set the twin
        // returns.
        var separator = SeparatorOf(target);
        var roots = StoredRoots(target, separator);

        var shortNames = ShortTypeNames();

        // Same restriction as the twin, for the same reason: a row whose type the server
        // cannot name is one the twin can never return, and a pair that disagrees is worse
        // than a pair that reports a little less.
        var knownTypes = shortNames.Keys.ToArray();

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var found = new List<PathItemDto>();
            foreach (var root in roots)
            {
                // The half-open range [root + separator, root + (separator + 1)) is exactly
                // "starts with root + separator". Anchoring on the separator is what keeps
                // /Movies/Ring from swallowing /Movies/Ring2.
                var lower = root + separator;
                var upper = root + (char)(separator + 1);

                // CompareTo is culture-sensitive in C#, but it is never executed here: EF
                // translates it to a plain SQL >= / <, which uses the column's BINARY
                // collation and is therefore ordinal. EF Core throws rather than evaluating
                // on the client, so the C# semantics cannot leak in. The ordinal-explicit
                // forms are not an option - measured, both
                // string.Compare(a, b, StringComparison.Ordinal) and string.CompareOrdinal
                // fail to translate at all.
                var rows = await dbContext.BaseItems
                    .AsNoTracking()
                    .Where(item => !item.IsVirtualItem
                                   && item.Path != null
                                   && knownTypes.Contains(item.Type)
                                   && (item.Path == root
                                       || (item.Path.CompareTo(lower) >= 0
                                           && item.Path.CompareTo(upper) < 0)))
                    .Select(item => new { item.Id, item.Type, item.Name, item.Path })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Report the expanded form, the same one the twin reports. Without this the
                // two routes would return the same items under different Path strings, and
                // Sorted() would even order them differently.
                found.AddRange(rows.Select(row => new PathItemDto(
                    row.Id,
                    shortNames[row.Type],
                    row.Name,
                    StoredPath.Expand(appHost, row.Path))));
            }

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
    /// Names an item the same way its database twin does.
    /// </summary>
    /// <remarks>
    /// <c>BaseItem.GetBaseItemKind()</c> parses <c>GetClientTypeName()</c>, which is a
    /// client-facing label and not always the name the type table carries: measured, the one
    /// row at the playlists directory came back as <c>ManualPlaylistsFolder</c> from this half
    /// and <c>PlaylistsFolder</c> from the twin - same id, same name, same path, two labels,
    /// because <c>PlaylistsFolder.GetClientTypeName()</c> overrides the former while
    /// <c>ItemTypeLookup</c> holds the latter. Looking the runtime type up in the same table
    /// the twin uses removes the second source.
    /// <para>
    /// The fallback is unreachable for anything the twin can return, since the twin only ever
    /// returns rows whose type is in that table; it exists so an item outside it is still
    /// named rather than throwing.
    /// </para>
    /// </remarks>
    /// <param name="item">The materialised item.</param>
    /// <param name="shortNames">Fully qualified type name to short name.</param>
    /// <returns>The short type name.</returns>
    private static string ShortTypeNameOf(BaseItem item, Dictionary<string, string> shortNames)
    {
        var typeName = item.GetType().FullName;
        return typeName is not null && shortNames.TryGetValue(typeName, out var name)
            ? name
            : item.GetBaseItemKind().ToString();
    }

    /// <summary>
    /// Lists every prefix the stored <c>Path</c> column has to be searched under to cover one
    /// expanded target.
    /// </summary>
    /// <remarks>
    /// Usually exactly one - the target in its stored spelling - and for any media path that
    /// is all it ever is, so the ordinary case still issues a single indexed range scan.
    /// <para>
    /// The extra entries exist for a target that <em>contains</em> one of the placeholder
    /// directories rather than lying inside it. <c>ReverseVirtualPath</c> leaves such a target
    /// alone, because neither directory appears in it as a substring, and no range built from
    /// it can reach a row stored under a placeholder. Those rows have to be fetched under the
    /// placeholder itself.
    /// </para>
    /// <para>
    /// "Strictly below" is what keeps the list free of duplicates: a target that <em>is</em>
    /// one of those directories has already been reversed into the placeholder by the first
    /// entry.
    /// </para>
    /// </remarks>
    /// <param name="target">The expanded target path.</param>
    /// <param name="separator">The separator that path uses.</param>
    /// <returns>The stored prefixes to scan, without duplicates.</returns>
    private List<string> StoredRoots(string target, char separator)
    {
        var roots = new List<string> { appHost.ReverseVirtualPath(target) };
        var prefix = target + separator;

        foreach (var token in VirtualPathTokens)
        {
            // A placeholder this server does not substitute expands to itself; there is no
            // directory to compare and nothing to add.
            var directory = appHost.ExpandVirtualPath(token);
            if (string.Equals(directory, token, StringComparison.Ordinal))
            {
                continue;
            }

            if (directory.StartsWith(prefix, StringComparison.Ordinal) && !roots.Contains(token))
            {
                roots.Add(token);
            }
        }

        return roots;
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
