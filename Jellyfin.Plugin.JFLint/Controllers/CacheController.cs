using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Clears Jellyfin's in-memory view of the library without restarting the server.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a route pair. Every query route in this plugin exists twice -
/// <c>X</c> over <c>ILibraryManager</c> and <c>XDB</c> straight from the database - so each
/// is the other's control. That only makes sense for a question with an answer to compare.
/// This one changes server state, and running a mutation twice by two routes would double
/// the effect rather than check it.
/// </para>
/// <para>
/// The whole reason for the route rather than a restart is that a restart is the only other
/// cure, and it interrupts playback.
/// </para>
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class CacheController(ILibraryManager libraryManager) : ControllerBase
{
    /// <summary>
    /// Drops the cached children of every physical library folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there is no path or id parameter</b>, although a targeted version looks
    /// tidier: the entries this clears are exactly the ones the database no longer holds,
    /// so their path resolves to nothing and their id resolves to nothing. There is no
    /// handle to aim with. Narrowing it would mean deriving an ancestor from the path as a
    /// string, which is guesswork about somebody else's data.
    /// </para>
    /// <para>
    /// The blunt version is affordable because the set is small - one entry per physical
    /// library folder, a handful in total - and because nothing is loaded here. Each folder
    /// simply forgets its list; the next query that needs one reloads it, and only the
    /// branches actually walked are ever read back.
    /// </para>
    /// <para>
    /// New entries no longer need this: <c>DeleteItemKeepFile</c> clears the folder it
    /// removed from. The route is for entries stranded before that fix, and for anything
    /// removed by a route that does not know about it - the stock
    /// <c>DELETE /Items/{itemId}</c> among them.
    /// </para>
    /// <para>
    /// <b>This changes server state</b>, so a caller asks first, the same as it would before
    /// pressing restart.
    /// </para>
    /// </remarks>
    /// <response code="200">The folders whose children were dropped.</response>
    /// <returns>The cleared folders, so a caller can report what it did rather than a count.</returns>
    [HttpPost("ForgetCachedChildren")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PathItemDto>> ForgetCachedChildren()
    {
        var cleared = FolderChildrenCache.ForgetAll(libraryManager);

        var rows = new List<PathItemDto>(cleared.Count);
        foreach (var folder in cleared)
        {
            rows.Add(new PathItemDto(
                folder.Id,
                folder.GetBaseItemKind().ToString(),
                folder.Name,
                folder.Path));
        }

        return Ok(rows);
    }
}
