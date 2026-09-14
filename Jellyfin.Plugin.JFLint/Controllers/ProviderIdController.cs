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
/// Finds provider ids that cannot identify anything, and removes them on request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why removing needs a route at all.</b> Jellyfin never deletes a provider id on a refresh
/// - <c>MetadataService.MergeBaseItemData</c> walks the source and writes into the target, so
/// an id the source no longer carries is left standing. Cleaning the NFO is therefore only half
/// the repair: the value stays in the database until something takes it out. And "set it to
/// empty" is not available either, because both public setters refuse a blank value
/// (<c>TrySetProviderId</c> returns false, <c>SetProviderId</c> throws). What is left is
/// removing the key.
/// </para>
/// <para>
/// <b>The occasion.</b> A sentinel <c>-1</c> is the established way to stop AniDB and AniList
/// guessing by name, and it worked - until a consumer read those ids and refused the whole
/// batch: <c>[0].anilist_id must be a positive integer</c> took down a sync of 518 series
/// because of one value. Measured here: 192 such values on 85 rows belonging to 13 series, all
/// of them <c>-1</c>.
/// </para>
/// <para>
/// <b>Removing them has a price and the route does not hide it.</b> Those sentinels suppress a
/// name-based match that has been wrong before - for <i>Archer</i> the name search lands on an
/// archery short from 1988. Taking them out restores that exposure the day such a provider is
/// installed again. This route reports the value it removed for exactly that reason: the
/// response is the material to put it back.
/// </para>
/// <para>
/// Requires elevation, like everything else here, and the query pair carries paths.
/// </para>
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used to
/// expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ProviderIdController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    private static readonly BaseItemKind[] WantedKinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];

    /// <summary>
    /// Gets provider ids that cannot identify anything, via <see cref="ILibraryManager"/>.
    /// </summary>
    /// <response code="200">Findings returned.</response>
    /// <returns>One row per implausible id, not per item.</returns>
    [HttpGet("InvalidProviderIds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProviderIdFindingDto>> GetInvalidProviderIds()
    {
        var findings = new List<ProviderIdFindingDto>();

        foreach (var item in libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = WantedKinds,
            Recursive = true
        }))
        {
            if (item.IsVirtualItem || item.ProviderIds is null)
            {
                continue;
            }

            foreach (var pair in item.ProviderIds)
            {
                var reason = ProviderIdRule.NonPositiveNumberReason(pair.Key, pair.Value);
                if (reason is null)
                {
                    continue;
                }

                findings.Add(new ProviderIdFindingDto
                {
                    ItemId = item.Id,
                    ItemType = item.GetBaseItemKind().ToString(),
                    Provider = pair.Key,
                    Value = pair.Value,
                    Reason = reason,
                    Name = item.Name,
                    SeriesName = (item as Episode)?.SeriesName,
                    Path = item.Path
                });
            }
        }

        return Ok(Sorted(findings));
    }

    /// <summary>
    /// Gets provider ids that cannot identify anything, straight from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>One row per implausible id.</returns>
    /// <remarks>
    /// The judgement runs in memory because the rule is per provider and not expressible as a
    /// single SQL predicate. The query narrows to the three item kinds and nothing else - any
    /// further filtering here would be a second, looser rule applied by one half only, which is
    /// how a pair stops being a control.
    /// </remarks>
    [HttpGet("InvalidProviderIdsDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProviderIdFindingDto>>> GetInvalidProviderIdsFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var wantedTypes = WantedKinds.Select(kind => itemTypeLookup.BaseItemKindNames[kind]).ToArray();

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(item => wantedTypes.Contains(item.Type) && !item.IsVirtualItem)
                .Select(item => new
                {
                    item.Id,
                    item.Type,
                    item.Name,
                    item.SeriesName,
                    item.Path,
                    Providers = item.Provider!
                        .Select(provider => new { provider.ProviderId, provider.ProviderValue })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var findings = new List<ProviderIdFindingDto>();
            foreach (var row in rows)
            {
                foreach (var provider in row.Providers)
                {
                    var reason = ProviderIdRule.NonPositiveNumberReason(provider.ProviderId, provider.ProviderValue);
                    if (reason is null)
                    {
                        continue;
                    }

                    findings.Add(new ProviderIdFindingDto
                    {
                        ItemId = row.Id,
                        ItemType = ShortTypeName(row.Type, wantedTypes),
                        Provider = provider.ProviderId!,
                        Value = provider.ProviderValue!,
                        Reason = reason,
                        Name = row.Name,
                        SeriesName = string.Equals(row.Type, wantedTypes[2], StringComparison.Ordinal) ? row.SeriesName : null,
                        Path = StoredPath.Expand(appHost, row.Path)
                    });
                }
            }

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Removes named provider ids from named items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route can do one thing.</b> It cannot touch a file, cannot delete an item, cannot
    /// write any other field and cannot select its own targets - the ids come from the caller,
    /// which got them from the query pair above and showed them to a user first. A wrong
    /// predicate upstream therefore costs the rows that were on screen; there is no filter here
    /// to get wrong.
    /// </para>
    /// <para>
    /// An id that is already absent is silently not reported rather than treated as an error:
    /// the caller's list may be a few seconds old, and a second caller having removed it first
    /// is the outcome that was wanted. What is reported is what this call actually changed.
    /// </para>
    /// <para>
    /// <b>The database row is not the whole story.</b> Jellyfin keeps items in memory, so a
    /// value removed here can still be answered from a cached instance until the server is
    /// restarted or <c>ForgetCachedChildren</c> is called.
    /// </para>
    /// </remarks>
    /// <param name="request">Which ids to remove, and from which rows.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">The ids that were removed, empty when none matched.</response>
    /// <response code="400">The request names no items or no providers.</response>
    /// <returns>One row per id actually removed.</returns>
    [HttpPost("RemoveProviderId")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RemovedProviderIdDto>>> RemoveProviderIdAsync(
        [FromBody] RemoveProviderIdRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            return BadRequest("ItemIds must name at least one item.");
        }

        if (request.Providers is null || request.Providers.Count == 0)
        {
            // Deliberately not "then remove them all": an empty list is far more likely to be
            // a caller that forgot to fill it than a user who meant every provider.
            return BadRequest("Providers must name at least one provider.");
        }

        var wanted = new HashSet<string>(request.Providers, StringComparer.OrdinalIgnoreCase);
        var removed = new List<RemovedProviderIdDto>();

        foreach (var itemId in request.ItemIds)
        {
            if (itemId.Equals(Guid.Empty))
            {
                continue;
            }

            var item = libraryManager.GetItemById(itemId);
            if (item?.ProviderIds is null)
            {
                continue;
            }

            // Collected before removing: the stored spelling is what has to be reported, and
            // mutating a dictionary while enumerating it is not allowed.
            var doomed = item.ProviderIds
                .Where(pair => wanted.Contains(pair.Key))
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))
                .ToList();

            if (doomed.Count == 0)
            {
                continue;
            }

            foreach (var pair in doomed)
            {
                item.ProviderIds.Remove(pair.Key);
                removed.Add(new RemovedProviderIdDto
                {
                    ItemId = item.Id,
                    Provider = pair.Key,
                    Value = pair.Value,
                    Name = item.Name
                });
            }

            // MetadataEdit, not MetadataImport: this is a deliberate change to the item rather
            // than the result of reading a provider, and the distinction is what keeps a later
            // refresh from treating it as importable data.
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return Ok(removed);
    }

    /// <summary>
    /// Sets one provider id on named rows, or removes it when no value is given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only way these values can be changed at all</b>, which is why it exists
    /// beside the NFO. <c>BaseNfoParser</c> builds its set of readable elements from
    /// <c>ProviderManager.GetExternalIdInfos</c> plus four hardcoded TMDb/IMDb keys, and calls
    /// <c>reader.Skip()</c> for anything else. Measured on the reference server: a Series
    /// reports seven external ids - Custom, Imdb, Tmdb, TvdbCollection, Tvdb, TvdbSlug, Zap2It -
    /// and no anime provider, because those plugins are uninstalled. An <c>&lt;anilistid&gt;</c>
    /// element in a tvshow.nfo is therefore skipped on every read, so editing the file changes
    /// nothing in the database however often it is rescanned.
    /// </para>
    /// <para>
    /// <b>Why setting beats removing.</b> A non-numeric value such as <c>none</c> suppresses a
    /// provider's name search exactly as a <c>-1</c> did - both AniDB's and AniList's series
    /// providers gate on <c>string.IsNullOrEmpty</c> and never parse the id on that path - while
    /// a consumer that reads ids numerically drops it instead of sending it. Removing the key
    /// satisfies the consumer too, but gives up the suppression, and for one series here the
    /// name search lands on an unrelated 1988 short. Setting keeps both.
    /// </para>
    /// <para>
    /// Blank means remove, and it has to: Jellyfin will not store an empty id. The removal goes
    /// through <c>ProviderIds.Remove</c> rather than a setter for the same reason.
    /// </para>
    /// <para>
    /// Like every write here, the caller supplies the ids. The route has no filter of its own,
    /// cannot touch a file and cannot write any field but this one.
    /// </para>
    /// </remarks>
    /// <param name="request">Which rows, which provider, which value.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">What changed, with the value that was there before.</response>
    /// <response code="400">The request names no items or no provider.</response>
    /// <returns>One row per item actually changed.</returns>
    [HttpPost("SetProviderId")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RemovedProviderIdDto>>> SetProviderIdAsync(
        [FromBody] SetProviderIdRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            return BadRequest("ItemIds must name at least one item.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return BadRequest("Provider must be given.");
        }

        if (request.Provider.Contains('=', StringComparison.Ordinal))
        {
            // Jellyfin's own setter refuses this, and for a reason that bites later rather
            // than now: a key containing '=' cannot be deserialised back out of the database.
            return BadRequest("Provider must not contain '='.");
        }

        var remove = string.IsNullOrWhiteSpace(request.Value);
        var changed = new List<RemovedProviderIdDto>();

        foreach (var itemId in request.ItemIds)
        {
            if (itemId.Equals(Guid.Empty))
            {
                continue;
            }

            var item = libraryManager.GetItemById(itemId);
            if (item?.ProviderIds is null)
            {
                continue;
            }

            // The stored spelling, which may differ from the one asked for - AniDb and AniDB
            // both occur here. Matching case-insensitively and reporting what was found keeps
            // a caller from having to guess, and stops a "set" from quietly adding a second
            // key that differs only in case.
            var existingKey = item.ProviderIds.Keys
                .FirstOrDefault(k => string.Equals(k, request.Provider, StringComparison.OrdinalIgnoreCase));
            var previous = existingKey is null ? null : item.ProviderIds[existingKey];

            if (remove)
            {
                if (existingKey is null)
                {
                    continue;
                }

                item.ProviderIds.Remove(existingKey);
            }
            else
            {
                if (string.Equals(previous, request.Value, StringComparison.Ordinal))
                {
                    // Already what was asked for. Writing it again would cost a repository
                    // round trip and report a change that did not happen.
                    continue;
                }

                if (existingKey is not null)
                {
                    item.ProviderIds.Remove(existingKey);
                }

                item.ProviderIds[request.Provider] = request.Value!;
            }

            changed.Add(new RemovedProviderIdDto
            {
                ItemId = item.Id,
                Provider = existingKey ?? request.Provider,
                Value = previous ?? string.Empty,
                Name = item.Name
            });

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return Ok(changed);
    }

    /// <summary>
    /// Turns a stored, fully qualified type name into the short one the responses carry.
    /// </summary>
    /// <param name="storedType">The value of <c>BaseItemEntity.Type</c>.</param>
    /// <param name="wantedTypes">The three stored names, in Movie/Series/Episode order.</param>
    /// <returns>The short name.</returns>
    private static string ShortTypeName(string? storedType, string[] wantedTypes)
    {
        if (string.Equals(storedType, wantedTypes[0], StringComparison.Ordinal))
        {
            return nameof(BaseItemKind.Movie);
        }

        return string.Equals(storedType, wantedTypes[1], StringComparison.Ordinal)
            ? nameof(BaseItemKind.Series)
            : nameof(BaseItemKind.Episode);
    }

    /// <summary>
    /// Orders findings the same way on both routes, so the pair can be read line by line.
    /// </summary>
    /// <param name="findings">The findings to order.</param>
    /// <returns>The findings by name, then provider, then item id.</returns>
    /// <remarks>
    /// The item id is a tiebreaker rather than decoration, and here it is load-bearing: one
    /// merged series has many rows carrying the same name and the same provider, so without it
    /// the two halves would agree on the set and differ on the order.
    /// </remarks>
    private static List<ProviderIdFindingDto> Sorted(IEnumerable<ProviderIdFindingDto> findings)
        => findings
            .OrderBy(finding => finding.Name is null)
            .ThenBy(finding => finding.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.ItemId)
            .ToList();
}
