using MediaBrowser.Controller;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Converts between the path Jellyfin stores and the path it hands out.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin does not persist the path it shows. On write it swaps the metadata and data
/// directories for the placeholders <c>%MetadataPath%</c> and <c>%AppDataPath%</c>
/// (<c>BaseItemRepository.GetPathToSave</c> -> <c>IServerApplicationHost.ReverseVirtualPath</c>)
/// and swaps them back as the last step of materialising an item (<c>BaseItemRepository.Map</c>,
/// <c>BaseItemMapper</c> on v12).
/// </para>
/// <para>
/// Every route in this plugin exists twice so that each half is the other's cross-check, and
/// the halves read from the two different sides of that substitution: the library half sees
/// the expanded path, the database half sees the stored one. Any database half that emits,
/// sorts on, or reasons about a path therefore has to expand it first, or the pair reports
/// the same rows under two different strings - which already cost this plugin one released
/// defect on <c>ItemsByPathDB</c>.
/// </para>
/// <para>
/// Kept as one helper rather than a call at each site on purpose: the sites are easy to add
/// and easy to forget, and a missed one is invisible until a library happens to sit under one
/// of those two directories.
/// </para>
/// </remarks>
internal static class StoredPath
{
    /// <summary>
    /// Turns a stored path into the one a materialised item reports.
    /// </summary>
    /// <param name="appHost">The application host that owns the substitution.</param>
    /// <param name="storedPath">The value straight out of the <c>Path</c> column.</param>
    /// <returns>The expanded path, or null when there was none.</returns>
    public static string? Expand(IServerApplicationHost appHost, string? storedPath)
        => storedPath is null ? null : appHost.ExpandVirtualPath(storedPath);

    /// <summary>
    /// Turns a caller-supplied real path into the form the column holds, so it can be
    /// compared against stored values.
    /// </summary>
    /// <param name="appHost">The application host that owns the substitution.</param>
    /// <param name="path">The expanded path.</param>
    /// <returns>The path as it would have been stored.</returns>
    public static string Reverse(IServerApplicationHost appHost, string path)
        => appHost.ReverseVirtualPath(path);
}
