using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Drops Jellyfin's in-memory folder children so the next query reloads them from the
/// database.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists for.</b> An item removed from the database keeps appearing in
/// any query that carries no user context, until the server is restarted. Measured on
/// 10.11.11 over three such entries: present in
/// <c>GET /Items?Recursive=true&amp;IncludeItemTypes=Series</c> without a <c>userId</c>,
/// absent from the same query with one, and absent from the database altogether.
/// </para>
/// <para>
/// <b>Why <c>LibraryManager.DeleteItem</c> does not prevent it.</b> It ends with
/// <c>if (parent is Folder folder) { folder.Children = null; }</c>, and that parent comes
/// from <c>item.GetParent()</c> - which resolves through <c>LibraryManager.GetItemById</c>,
/// so out of the LRU cache or freshly retrieved. But a folder's own children come from
/// <c>Folder.LoadChildren()</c> → <c>GetCachedChildren()</c>, which calls
/// <c>ItemRepository.GetItemList(...)</c> <b>directly</b> and therefore never registers
/// what it builds. So the object sitting in a parent's <c>_children</c> and the object
/// returned for the same id by <c>GetItemById</c> are two different instances. Jellyfin
/// nulls one; <c>Folder.AddChildrenToList</c> walks the other, purely along object
/// references. Measured, not inferred: the three entries above were absent from
/// <c>GET /Items?ParentId=&lt;their parent&gt;</c> - which resolves that parent by id - while
/// still present in the walk from the root.
/// </para>
/// <para>
/// <b>Where the second instance comes from, and it is higher up than it looks.</b> A
/// user-less query does not start at the user root: <c>ItemsController</c> asks
/// <c>GetParentItem(null, null)</c>, which returns <c>LibraryManager.RootFolder</c> - the
/// <c>AggregateFolder</c>. And <c>AggregateFolder.LoadChildren()</c> takes
/// <c>base.LoadChildren()</c> the first time it is called, so its <c>_children</c> holds
/// <b>repository-built physical folder objects</b>; only a later reload resolves them by id.
/// <c>CollectionFolder.GetPhysicalFolders(true)</c> meanwhile uses
/// <c>LibraryManager.GetItemById</c>. So the divergence is not below the physical library
/// folder - it <b>is</b> the physical library folder.
/// </para>
/// <para>
/// Both therefore have to be cleared: the instances reachable by id, and the aggregate root
/// that holds the others. Nulling <c>RootFolder.Children</c> is also what makes the two
/// views converge, because the aggregate root has recorded its child ids by then and its
/// next load resolves them through <c>GetItemById</c> - the same objects the collection
/// folders use.
/// </para>
/// <para>
/// Nothing here walks the tree downwards: the <c>Children</c> getter <i>populates</i>, so
/// enumerating it to find folders would load the library into memory in order to throw it
/// away.
/// </para>
/// <para>
/// <b>Concurrency.</b> Assigning <c>null</c> while another request enumerates the list is
/// safe: the enumerator holds the old list, which is not mutated. Two readers arriving
/// afterwards can both run <c>LoadChildren()</c>, costing a duplicate read and no more.
/// <c>LibraryManager.DeleteItem</c> does exactly the same thing on every delete.
/// </para>
/// </remarks>
internal static class FolderChildrenCache
{
    /// <summary>
    /// Drops the cached children of every physical library folder.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The folders that were cleared.</returns>
    public static IReadOnlyList<Folder> ForgetAll(ILibraryManager libraryManager)
    {
        var roots = GetLibraryRoots(libraryManager);
        foreach (var root in roots.Values)
        {
            root.Children = null;
        }

        DetachAggregateRoot(libraryManager);
        return roots.Values.ToList();
    }

    /// <summary>
    /// Drops the aggregate root's own list of physical folders.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <remarks>
    /// The step that actually reaches a user-less query. Clearing the folders returned by
    /// <c>GetItemById</c> is not enough on its own, because the aggregate root holds
    /// <b>different objects</b> for them - see the remarks on this class. Its next load
    /// resolves the ids it has already recorded through <c>GetItemById</c>, so afterwards
    /// both views share one set of instances.
    /// </remarks>
    public static void DetachAggregateRoot(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        libraryManager.RootFolder.Children = null;
    }

    /// <summary>
    /// Drops the cached children of the physical library folder an item sits under.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="item">The item to locate.</param>
    /// <returns>The folder that was cleared, or null when the item sits under none.</returns>
    /// <remarks>
    /// Call this <b>before</b> deleting: <c>LibraryManager.DeleteItem</c> runs
    /// <c>item.SetParent(null)</c>, after which the ancestors are no longer reachable from
    /// the item. Resolve first, assign afterwards.
    /// </remarks>
    public static Folder? FindLibraryRoot(ILibraryManager libraryManager, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var roots = GetLibraryRoots(libraryManager);
        if (roots.Count == 0)
        {
            return null;
        }

        // The item itself may be a library root; GetParents does not include it.
        if (roots.TryGetValue(item.Id, out var self))
        {
            return self;
        }

        foreach (var ancestor in item.GetParents())
        {
            if (roots.TryGetValue(ancestor.Id, out var root))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects the physical library folders, keyed by id.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The folders, deduplicated - one physical folder can serve more than one
    /// collection folder.</returns>
    /// <remarks>
    /// Enumerating <c>GetUserRootFolder().Children</c> is cheap: those are the collection
    /// folders, a handful of items. A <c>CollectionFolder</c> keeps no children of its own -
    /// its <c>Children</c> is computed on every read - so it needs nothing done to it, and
    /// its physical folders are what carry the stale lists.
    /// </remarks>
    private static Dictionary<Guid, Folder> GetLibraryRoots(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var roots = new Dictionary<Guid, Folder>();
        foreach (var collection in libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>())
        {
            foreach (var physical in collection.GetPhysicalFolders())
            {
                roots[physical.Id] = physical;
            }
        }

        return roots;
    }
}
