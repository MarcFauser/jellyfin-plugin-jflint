using System;
using System.Collections.Generic;
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
    private static readonly char[] RegionSeparators = ['-', '_'];

    /// <summary>
    /// A stored language code, read the way Jellyfin reads it.
    /// </summary>
    /// <param name="localization">The server's localization manager.</param>
    /// <param name="stored">The raw <c>Language</c> column.</param>
    /// <returns>The ISO 639-2/T code where the stored one is a 639-2/B code, else the stored
    /// value unchanged - null stays null, <c>und</c> stays <c>und</c>.</returns>
    public static string? AsJellyfinReadsIt(ILocalizationManager localization, string? stored)
        => stored is not null && localization.TryGetISO6392TFromB(stored, out var isoT) ? isoT : stored;

    /// <summary>
    /// Every code a track in the named language may carry, taken from the server's own language
    /// list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ILocalizationManager.FindLanguageInfo"/> resolves a two-letter code, either
    /// three-letter code or the English name - <c>de</c>, <c>deu</c>, <c>ger</c>, <c>German</c> -
    /// to one entry of <c>iso6392.txt</c>, read on <c>release-10.11.z</c> and <c>v12.1</c>. The
    /// codes returned are that entry's two-letter code and every three-letter one, so for German
    /// <c>de</c>, <c>deu</c> and <c>ger</c>: the bare two-letter form is not theoretical, 64
    /// tracks on the reference library carry it.
    /// </para>
    /// <para>
    /// The region is cut off before the lookup, as <see cref="IsIn"/> cuts it off a track: the
    /// list has <c>zh-hans</c> as an entry of its own whose two-letter code is <c>zh-hans</c>, so
    /// asking for it uncut would miss every track tagged plain <c>zh</c>. Only when the cut form
    /// names nothing is the whole value tried.
    /// </para>
    /// <para>
    /// The two lines load the list differently: 10.11 skips the 302 of 496 entries that have no
    /// two-letter code, 12 keeps them. A language such as <c>gsw</c> is therefore known to one line
    /// and not the other - that is the server's answer, not something to even out here.
    /// </para>
    /// </remarks>
    /// <param name="localization">The server's localization manager.</param>
    /// <param name="language">What the caller asked for.</param>
    /// <returns>The codes, compared ignoring case, or null when the server knows no such
    /// language.</returns>
    public static IReadOnlySet<string>? CodesOf(ILocalizationManager localization, string? language)
    {
        var wanted = language?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        var culture = localization.FindLanguageInfo(Bare(wanted)) ?? localization.FindLanguageInfo(wanted);
        if (culture is null)
        {
            return null;
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
        {
            codes.Add(culture.TwoLetterISOLanguageName);
        }

        foreach (var code in culture.ThreeLetterISOLanguageNames)
        {
            if (!string.IsNullOrEmpty(code))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>
    /// Whether a track's language is one of the given codes.
    /// </summary>
    /// <remarks>
    /// A regional tag counts for its language - <c>de-DE</c> is German; Matroska allows BCP 47
    /// tags beside the ISO 639-2 ones. A track with no tag, or with <c>und</c>, names no language
    /// and matches none, so a file whose only track says nothing is NOT taken for German: whether
    /// it is cannot be told from here, and the caller sees the track and decides.
    /// </remarks>
    /// <param name="tag">The track's language.</param>
    /// <param name="codes">What <see cref="CodesOf"/> returned.</param>
    /// <returns>True for a match.</returns>
    public static bool IsIn(string? tag, IReadOnlySet<string> codes)
        => !string.IsNullOrWhiteSpace(tag) && codes.Contains(Bare(tag.Trim()));

    /// <summary>
    /// A language tag without its region.
    /// </summary>
    /// <param name="tag">A trimmed tag.</param>
    /// <returns><c>de</c> for <c>de-DE</c> and <c>de_DE</c>, the tag itself otherwise.</returns>
    private static string Bare(string tag)
    {
        var cut = tag.IndexOfAny(RegionSeparators);
        return cut > 0 ? tag[..cut] : tag;
    }
}
