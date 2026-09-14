using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// Which ids to remove, and from which items.
/// </summary>
/// <remarks>
/// <b>Both lists are required and neither may be empty.</b> There is deliberately no "remove
/// everywhere" and no "remove every provider": the caller shows the finding list, the user
/// confirms it, and the route executes exactly that. A mistake in a predicate then costs the
/// rows that were on screen rather than the library - the same reasoning as
/// <c>DeleteItemKeepFile</c> taking one id instead of a filter.
/// </remarks>
public sealed record RemoveProviderIdRequest
{
    /// <summary>
    /// Gets the row ids to touch.
    /// </summary>
    public required IReadOnlyList<Guid> ItemIds { get; init; }

    /// <summary>
    /// Gets the provider names to remove, matched case-insensitively against the stored keys.
    /// </summary>
    /// <remarks>
    /// Case-insensitive because the stored spelling varies - <c>AniDb</c> and <c>AniDB</c> both
    /// occur - and a caller should not have to guess which one a row happens to use. The
    /// response reports the spelling that was actually removed.
    /// </remarks>
    public required IReadOnlyList<string> Providers { get; init; }
}
