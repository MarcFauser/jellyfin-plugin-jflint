using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Reads a stored stream language the way Jellyfin's own stream repository does.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Language</c> column holds what ffprobe wrote - <c>ger</c>, the bibliographic ISO
/// 639-2/B code - and <c>MediaStreamRepository.Map</c> turns it into <c>deu</c> through
/// <see cref="ILocalizationManager.TryGetISO6392TFromB"/> on the way out, on
/// <c>release-10.11.z</c> and <c>v12.1</c> alike. It is the only one of the six audio fields
/// this plugin reports that <c>Map</c> changes. Without this a route reading the column raw said
/// <c>ger</c> where the object model and <c>Fields=MediaStreams</c> say <c>deu</c>: measured at
/// 12.37.0.0's acceptance, 187 of 202 files, and <c>fre</c>, <c>chi</c> and <c>cze</c> the same.
/// </para>
/// <para>
/// One place for every route that reads stream languages from the database - it lived in
/// <c>DuplicateController</c> until <c>MediaInfoDB</c> needed it too, and two copies of one
/// rule are two rules that drift apart. Calling the server's lookup rather than keeping a table
/// here, so it cannot drift from the server either.
/// </para>
/// </remarks>
public static class StreamLanguage
{
    /// <summary>
    /// A stored language code, read the way Jellyfin reads it.
    /// </summary>
    /// <param name="localization">The server's localization manager.</param>
    /// <param name="stored">The raw <c>Language</c> column.</param>
    /// <returns>The ISO 639-2/T code where the stored one is a 639-2/B code, else the stored
    /// value unchanged - null stays null, <c>und</c> stays <c>und</c>.</returns>
    public static string? AsJellyfinReadsIt(ILocalizationManager localization, string? stored)
        => stored is not null && localization.TryGetISO6392TFromB(stored, out var isoT) ? isoT : stored;
}
