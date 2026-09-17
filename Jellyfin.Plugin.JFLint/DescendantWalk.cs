using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Collects every row hanging beneath an item by following <c>ParentId</c> in the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than written twice, for the same reason the rule classes here are.</b> Two
/// callers ask this question - the refusal in <c>DeleteItemKeepFile</c> and the
/// <c>DescendantsDB</c> route that reports it - and a caller checking the second before
/// triggering the first is only protected if both count the same rows. Two hand-written walks
/// would be two chances to answer differently, and the disagreement would show up as a delete
/// that was refused after a check said it would not be, or worse the other way round.
/// </para>
/// <para>
/// <b>Why it exists at all: the object-model route is filtered on Jellyfin 12.</b>
/// <c>Folder.GetRecursiveChildren</c> resolves through <c>Children</c> -&gt;
/// <c>LoadChildren</c> -&gt; <c>GetCachedChildren()</c>, which issues
/// <c>ItemRepository.GetItemList(new InternalItemsQuery { Parent = this, … })</c>. That query
/// sets no <c>OwnerIds</c>, no <c>ExtraTypes</c> and no <c>IncludeOwnedItems</c>, so
/// <c>BaseItemRepository.TranslateQuery</c> appends
/// <c>PrimaryVersionId == null &amp;&amp; (OwnerId == null || ExtraType != null)</c> - and the
/// answer silently omits alternate versions and owned non-extra rows. Measured across both
/// shipped trees, that predicate occurs <b>0</b> times in 10.11 and <b>7</b> in v12, the
/// positive control being that the 10.11 tree mentions the column in 31 files.
/// </para>
/// </remarks>
internal static class DescendantWalk
{
    /// <summary>
    /// Every row beneath an item, however deep.
    /// </summary>
    /// <param name="dbContext">An open database context, owned by the caller.</param>
    /// <param name="id">The item to walk beneath.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per descendant row, empty when there is none. The item itself is
    /// never included.</returns>
    /// <remarks>
    /// <para>
    /// Walked one level at a time rather than as a recursive CTE, because EF Core cannot express
    /// one and the depth here is a season or two - a handful of round trips, each an indexed
    /// lookup on <c>ParentId</c>.
    /// </para>
    /// <para>
    /// <b><c>seen</c> is what terminates the loop</b>, not a depth cap. A cycle in
    /// <c>ParentId</c> would itself be a defect, and with <c>seen</c> it costs one extra round
    /// trip instead of hanging; a cap on top would be a second guard for a case the first
    /// already covers.
    /// </para>
    /// <para>
    /// The frontier is a <c>List&lt;Guid?&gt;</c> so <c>Contains</c> translates against the
    /// nullable column as a plain <c>IN</c>. With a <c>List&lt;Guid&gt;</c> the comparison needs
    /// <c>ParentId.Value</c>, which is the shape EF is least reliable about translating.
    /// </para>
    /// <para>
    /// <b>It follows <c>ParentId</c> and only <c>ParentId</c>, so an OWNED row inside the same
    /// folder is not a descendant here.</b> Measured on a release of this library: the walk
    /// found two pathless <c>Season</c> rows that <c>ItemsByPathDB</c> structurally cannot see,
    /// and <c>ItemsByPathDB</c> found a <c>Video</c> - <c>backdrops/theme-youtube-search.webm</c>
    /// - that the walk does not reach, because a theme hangs off <c>OwnerId</c> rather than
    /// <c>ParentId</c>. <b>Neither route is a superset of the other</b>, and a caller that needs
    /// "everything under this directory" wants both.
    /// </para>
    /// <para>
    /// <b>Not a regression, and not silently accepted either.</b> The guard this replaced walked
    /// <c>Folder.Children</c>, which is the same <c>ParentId</c> edge, so the owner edge was
    /// never covered by either implementation - what changed is that there is now a second
    /// instrument, which is why it became visible at all. Following <c>OwnerId</c> as well would
    /// need its own version branch (<c>string?</c> on 10.11, <c>Guid?</c> on v12, the same shift
    /// <c>PrimaryVersionId</c> made) and has not been built, because whether it can bite is
    /// unmeasured: it needs a folder whose <i>only</i> remaining child is owned by it, and
    /// nobody has counted whether such a row exists. Written down rather than assumed away.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<(Guid Id, string Type, string? Name, string? Path)>> FromDatabaseAsync(
        JellyfinDbContext dbContext,
        Guid id,
        CancellationToken cancellationToken)
    {
        var found = new List<(Guid Id, string Type, string? Name, string? Path)>();
        var seen = new HashSet<Guid> { id };
        var frontier = new List<Guid?> { id };

        while (frontier.Count > 0)
        {
            var parents = frontier;
            var rows = await dbContext.BaseItems
                .AsNoTracking()
                .Where(row => parents.Contains(row.ParentId))
                .Select(row => new { row.Id, row.Type, row.Name, row.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            frontier = new List<Guid?>();
            foreach (var row in rows)
            {
                if (!seen.Add(row.Id))
                {
                    continue;
                }

                found.Add((row.Id, row.Type, row.Name, row.Path));
                frontier.Add(row.Id);
            }
        }

        return found;
    }
}
