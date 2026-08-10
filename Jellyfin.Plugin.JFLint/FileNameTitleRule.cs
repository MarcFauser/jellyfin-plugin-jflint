using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Decides whether an entry's title is nothing but the file or folder it came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a verbatim port, and that is the point.</b> The rule also exists in the calling
/// tool, whose slow fallback computes the same set - so the two halves of this plugin's route
/// pair and that fallback are each other's controls only for as long as all three reach the
/// same verdict on every row. A better rule that differs is worse than this one, because it
/// destroys the comparison. Any change belongs upstream first.
/// </para>
/// <para>
/// The clause that exonerates a well-named entry rests on <see cref="Evidence"/>, and a
/// reconstruction of it was measured to fail: a trailing-group test written as <c>-XYZ</c>
/// anywhere matches the hyphen inside <c>eps1.1_ones-and-zer0es.mpeg</c> and keeps in
/// precisely the rows the clause exists to let out. The anchor at the end of the pattern is
/// the whole difference.
/// </para>
/// </remarks>
public static class FileNameTitleRule
{
    /// <summary>
    /// The finding is reported because the title reads as a dotted file name.
    /// </summary>
    public const string DottedName = nameof(DottedName);

    /// <summary>
    /// The finding is reported because the title <b>is</b> the last path segment.
    /// </summary>
    public const string SameAsFileName = nameof(SameAsFileName);

    /// <summary>
    /// The provider ids that make an entry "identified".
    /// </summary>
    /// <remarks>
    /// The narrow reading, matching the calling tool's own no-id scan: <c>TvdbSlug</c> and
    /// <c>TmdbCollection</c> do not count. Measured to change nothing on the reference
    /// library - not one of its 44,288 entries carries only soft ids - so the two sides agree
    /// by construction rather than because the data cannot tell them apart.
    /// </remarks>
    public static readonly IReadOnlySet<string> RealProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Tmdb", "Tvdb", "Imdb", "AniList", "AniDb", "AniSearch", "TvRage", "Zap2It"
    };

    // What a release name carries and a title does not. Written on the raw string with token
    // boundaries in the pattern, so 1080p matches as a token and not as a substring, and the
    // group tag matches only at the end.
    private static readonly Regex Evidence = new(
        @"(^|[.\-_])(\d{3,4}p|[xh]\.?26[45]|HEVC|AVC|BluRay|BDRip|WEB|WEBRip|WEB-?DL|HDTV|DVDRip|REMUX|UHD)([.\-_]|$)"
        + @"|(^|[.\-_])(German|English|Deutsch|MULTi|DL|AC3|DTS|EAC3|DDP?5|TrueHD|Atmos|HDR|DV|SDR)([.\-_]|$)"
        + @"|(^|[.\-_])(iNTERNAL|REPACK|PROPER|UNRATED|EXTENDED|COMPLETE|UNCUT|ANiME|RETAIL)([.\-_]|$)"
        + @"|S\d{1,2}E\d{1,3}|(^|[.\-_])E\d{2,4}([.\-_]|$)|(^|[.\-_])OVA\d*([.\-_]|$)|-[A-Za-z0-9]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Decides whether an entry is a finding, and why.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="path">The entry's path, which may be null.</param>
    /// <param name="identified">Whether the entry carries a real provider id.</param>
    /// <returns>The reasons it is reported, empty when it is not a finding.</returns>
    public static IReadOnlyList<string> Evaluate(string? name, string? path, bool identified)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<string>();
        }

        var leaf = Leaf(path);
        var reasons = new List<string>(2);

        if (LooksLikeAFileName(name) && !Cleared(name, path, leaf, identified))
        {
            reasons.Add(DottedName);
        }

        if (!identified && IsTheLeaf(name, leaf))
        {
            reasons.Add(SameAsFileName);
        }

        return reasons;
    }

    /// <summary>
    /// Whether a set of provider keys makes an entry identified.
    /// </summary>
    /// <param name="providerKeys">The provider names carried by the entry.</param>
    /// <returns>True when at least one is a real id.</returns>
    public static bool IsIdentified(IEnumerable<string>? providerKeys)
        => providerKeys is not null && providerKeys.Any(RealProviderIds.Contains);

    /// <summary>
    /// Half A: the title reads as a dotted file name.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <returns>True when it does.</returns>
    /// <remarks>
    /// The "length >= 2" floor is what spares acronyms: <c>S.W.A.T.</c> has four dots and no
    /// piece of two characters, so it never reaches three.
    /// <para>
    /// Public because <see cref="Models.LayoutFindingKind.PerEpisodeFolder"/> asks the same
    /// question of a season's name. Sharing the predicate is the point: two rules that look
    /// alike are two rules that drift apart.
    /// </para>
    /// </remarks>
    public static bool LooksLikeAFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        var dots = name.Length - name.Replace(".", string.Empty, StringComparison.Ordinal).Length;
        if (dots < 2)
        {
            return false;
        }

        return name.Split('.').Count(piece => piece.Length >= 2) >= 3;
    }

    /// <summary>
    /// Half B: the title is the last path segment, with or without its extension.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="leaf">The last segment of the entry's path.</param>
    /// <returns>True when they are the same.</returns>
    private static bool IsTheLeaf(string name, string leaf)
    {
        if (string.IsNullOrEmpty(leaf))
        {
            return false;
        }

        var dot = leaf.LastIndexOf('.');
        var bare = dot > 0 ? leaf[..dot] : leaf;
        return string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The exoneration clause: a well-named entry that merely happens to contain dots.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="path">The entry's path.</param>
    /// <param name="leaf">The last segment of that path.</param>
    /// <param name="identified">Whether the entry carries a real provider id.</param>
    /// <returns>True when the entry should not be reported.</returns>
    /// <remarks>
    /// All three facts must hold. The folder half is load-bearing rather than belt and
    /// braces: a film's path is the video file, so the release name usually sits one level
    /// up - without it a hundred films whose file is <c>bhd-starf-720p.mkv</c> drop out.
    /// Nothing is normalised, deliberately - not dots against spaces, not umlauts against
    /// transliterations.
    /// </remarks>
    private static bool Cleared(string name, string? path, string leaf, bool identified)
    {
        if (!identified || Evidence.IsMatch(name))
        {
            return false;
        }

        var parent = Leaf(Parent(path));
        return !leaf.StartsWith(name, StringComparison.OrdinalIgnoreCase)
               && !parent.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The last segment of a path, either separator.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The segment, or empty.</returns>
    private static string Leaf(string? path)
        => string.IsNullOrEmpty(path)
            ? string.Empty
            : path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? string.Empty;

    /// <summary>
    /// Everything above the last segment.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The parent path, or empty.</returns>
    private static string Parent(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut > 0 ? trimmed[..cut] : string.Empty;
    }
}
