using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Removes a library entry while leaving the media file untouched.
/// </summary>
/// <remarks>
/// <para>
/// The stock <c>DELETE /Items/{itemId}</c> hardcodes
/// <c>new DeleteOptions { DeleteFileLocation = true }</c>, and the flag is not reachable
/// over HTTP - checked against the running server's own OpenAPI document. So a tool that
/// wants to clear a stale entry, one whose file is already gone, has no route that cannot
/// also delete media. This one closes that gap.
/// </para>
/// <para>
/// The safety here is structural rather than conditional. There is no parameter to get
/// wrong: the route cannot delete a file at all, so a stale row that turns out to be fresh
/// after all costs a rescan rather than the media.
/// </para>
/// <para>
/// Requires elevation - stricter than the stock delete, which takes any authenticated
/// user, and deliberately not looser.
/// </para>
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="authorizationContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface, used to
/// name a blocking row's kind without hardcoding a stored type name.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used to
/// expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context. The refusal counts
/// descendants here rather than through the object model; see <see cref="DescendantsAsync"/> for
/// what that cost before it did.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ItemRemovalController(
    ILibraryManager libraryManager,
    IAuthorizationContext authorizationContext,
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    // Enough to identify what is in the way without turning a refusal into a data dump.
    // The count in the response stays exact however many are listed.
    private const int BlockingChildSampleSize = 20;

    /// <summary>
    /// Removes one item from the library and leaves its file on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One id, one item, never recursive.</b> <c>LibraryManager.DeleteItem</c> hands the
    /// item and every recursive descendant to the repository as a single batch, and that
    /// batch is the path which trips the <c>UserData</c> UNIQUE constraint of jellyfin#16120
    /// - fixed in v12, not in 10.11.x. A caller is expected to delete children first,
    /// deepest last; this route refuses a folder that still has descendants rather than
    /// trusting it to remember.
    /// </para>
    /// <para>
    /// The item id is taken as a string and parsed here on purpose. A <c>:guid</c> route
    /// constraint would make a malformed id fail to match the route, and ASP.NET answers a
    /// route that does not exist with <b>404</b> - which is exactly the signal a caller
    /// reads as "the plugin is not installed", sending it back to the stock route that does
    /// delete files. 404 has to keep meaning one thing.
    /// </para>
    /// </remarks>
    /// <param name="itemId">The id of the item to remove.</param>
    /// <response code="204">The entry was removed; the file was not touched.</response>
    /// <response code="400">The id is not a usable item id.</response>
    /// <response code="401">The caller may not delete this item.</response>
    /// <response code="409">
    /// The item is a folder that still has descendants. The body is a
    /// <see cref="DeleteConflictDto"/> naming up to twenty of them, because a bare count
    /// leaves a caller with nowhere to look.
    /// </response>
    /// <response code="410">No such item - it is already gone.</response>
    /// <returns>An <see cref="ActionResult"/> carrying the outcome.</returns>
    [HttpDelete("DeleteItemKeepFile/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(DeleteConflictDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult> DeleteItemKeepFileAsync(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id) || id.Equals(Guid.Empty))
        {
            return BadRequest("itemId must be a non-empty GUID.");
        }

        var item = libraryManager.GetItemById(id);
        if (item is null)
        {
            // 410, not 404: the route ran and found nothing, which a caller counts as
            // "already gone". 404 is reserved for the plugin being absent altogether, and
            // the two must stay distinguishable.
            return StatusCode(StatusCodes.Status410Gone, "No item with that id.");
        }

        // Mirrors the stock controller: an API key carries no user, and then there is no
        // per-user permission to check. Worth knowing that this is therefore no protection
        // at all for an API-key caller - the elevation policy on the controller is.
        var authorization = await authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.User is not null && !item.CanDelete(authorization.User))
        {
            return Unauthorized("Unauthorized access");
        }

        if (item is Folder)
        {
            // The refusal names its blockers rather than only counting them. A caller that
            // is told "1 descendant" and can find none over HTTP has nowhere to go; one
            // that is told which id, type and path is in the way can act or report it.
            var children = await DescendantsAsync(id).ConfigureAwait(false);
            if (children.Count > 0)
            {
                var sample = new List<BlockingChildDto>(Math.Min(children.Count, BlockingChildSampleSize));
                foreach (var child in children)
                {
                    if (sample.Count == BlockingChildSampleSize)
                    {
                        break;
                    }

                    sample.Add(child);
                }

                return Conflict(new DeleteConflictDto(children.Count, sample));
            }
        }

        // Resolved BEFORE the delete: DeleteItem runs item.SetParent(null), and after that
        // the ancestors cannot be reached from the item any more.
        var libraryRoot = FolderChildrenCache.FindLibraryRoot(libraryManager, item);

        // Both flags are written out, including the one the class already defaults to.
        // DeleteOptions belongs to MediaBrowser.Controller, and its defaults are that
        // project's implementation detail, not a promise to this one - its constructor
        // already sets the other field, which shows the file is where defaults get decided.
        // A future release lining DeleteFileLocation up with the controller that always
        // passes true would turn this route into the thing it exists to avoid, and nothing
        // here would fail to compile.
        //
        // DeleteFromExternalProvider departs from the stock route on purpose: a stale entry
        // is a bookkeeping fault, not a deletion. Telling an external service that the
        // episode was removed would push our error outward into something that was right.
        libraryManager.DeleteItem(
            item,
            new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false },
            notifyParentItem: true);

        // DeleteItem nulls the children of the parent it resolved by id, which is not the
        // instance a user-less query walks - so without this the row is gone from the
        // database and still answered from memory until the server restarts.
        //
        // Both steps are needed, and the second is the one that reaches the query: the
        // aggregate root keeps its OWN objects for the physical library folders, so
        // clearing the one resolved by id leaves the walked instance untouched. See
        // FolderChildrenCache.
        if (libraryRoot is not null)
        {
            libraryRoot.Children = null;
        }

        FolderChildrenCache.DetachAggregateRoot(libraryManager);

        return NoContent();
    }

    /// <summary>
    /// Every row still hanging beneath an item, read from the database rather than the object
    /// model.
    /// </summary>
    /// <param name="id">The item about to be deleted.</param>
    /// <returns>One entry per descendant row, empty when there is none.</returns>
    /// <remarks>
    /// <para>
    /// <b>This used to be <c>folder.GetRecursiveChildren(false)</c>, and on Jellyfin 12 that
    /// guard was blind.</b> The chain is <c>GetRecursiveChildren</c> -&gt; <c>Children</c> -&gt;
    /// <c>LoadChildren</c> -&gt; <c>GetCachedChildren()</c>, and the last one issues
    /// <c>ItemRepository.GetItemList(new InternalItemsQuery { Parent = this, … })</c>. That
    /// query sets no <c>OwnerIds</c>, no <c>ExtraTypes</c> and no <c>IncludeOwnedItems</c>, so
    /// <c>BaseItemRepository.TranslateQuery</c> appends
    /// <c>PrimaryVersionId == null &amp;&amp; (OwnerId == null || ExtraType != null)</c> to it -
    /// and the guard therefore could not see alternate-version rows or owned non-extra rows.
    /// </para>
    /// <para>
    /// <b>The guard was complete on 10.11 and narrowed silently at the version bump.</b>
    /// Measured across both shipped trees: <c>PrimaryVersionId == null</c> occurs <b>0</b> times
    /// in 10.11 and <b>7</b> in v12, the positive control being that the 10.11 tree mentions the
    /// column in 31 files, so the search was not blind there.
    /// </para>
    /// <para>
    /// <b>What it cost, measured live rather than argued.</b> On a release holding one episode
    /// as two stacked files, <c>ItemsByPathDB</c> reported the folder plus
    /// <c>…teil-1-720p.mkv</c> and <c>…teil-2-720p.mkv</c> while <c>ItemsByPath</c> reported the
    /// folder and <c>teil-1</c> alone. Delete the visible half, ask to delete the folder, and
    /// the old guard counted zero children: the folder went, the second row stayed behind with a
    /// dangling <c>ParentId</c> - which is the exact damage <c>OrphanedItem</c> and
    /// <c>OrphanRowsDB</c> exist to report, caused by the route meant to prevent it.
    /// </para>
    /// <para>
    /// <b>No pair could ever have caught this.</b> This route has no twin, and it is the only
    /// place in the plugin that reaches <c>BaseItems</c> through neither
    /// <c>dbContext.BaseItems</c> nor <c>ILibraryManager.GetItemList</c>. It was found by asking
    /// what a sweep of the sixteen pairs had left out, not by the sweep.
    /// </para>
    /// <para>
    /// <b>The walk itself is shared with the <c>DescendantsDB</c> route</b>, see
    /// <see cref="DescendantWalk"/>. A caller is expected to ask that route before triggering
    /// this one, and it is only protected if both count the same rows - two hand-written walks
    /// would be two chances to answer differently, and the disagreement would surface as a
    /// delete refused after a check said it would not be, or worse the other way round.
    /// </para>
    /// <para>
    /// <b>Only folders are asked.</b> A row pointing its <c>ParentId</c> at a non-folder would
    /// be orphaned by this route just as silently, and that case is deliberately left alone
    /// rather than fixed in passing: it is a different defect, nobody has measured that it
    /// occurs, and widening a delete guard is not a change to make on the way past.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<BlockingChildDto>> DescendantsAsync(Guid id)
    {
        // Reversed once per call, not per row: BaseItemKindNames maps kind -> stored type name
        // and the rows carry the stored name. Falling back to the raw value rather than to a
        // placeholder - an unknown type name is worth seeing verbatim, and a blocker the caller
        // cannot name is worse than an ugly one.
        var kindOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in itemTypeLookup.BaseItemKindNames)
        {
            kindOf[pair.Value] = pair.Key.ToString();
        }

        var dbContext = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await DescendantWalk.FromDatabaseAsync(dbContext, id, default).ConfigureAwait(false);

            var found = new List<BlockingChildDto>(rows.Count);
            foreach (var row in rows)
            {
                found.Add(new BlockingChildDto(
                    row.Id,
                    kindOf.TryGetValue(row.Type, out var kind) ? kind : row.Type,
                    row.Name,
                    StoredPath.Expand(appHost, row.Path)));
            }

            return found;
        }
    }
}
