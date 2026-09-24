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
    /// The finding is reported because the title is the <b>start</b> of the last path segment,
    /// going on at one of Jellyfin's cut characters - see <see cref="StartsTheLeaf"/>.
    /// </summary>
    /// <remarks>
    /// Its own reason rather than <see cref="SameAsFileName"/>, although the decision is one
    /// call: that name says the title IS the segment, and a reason that says something untrue
    /// is worse than a coarser one. The calling tool reads this field without comparing it.
    /// </remarks>
    public const string StartOfFileName = nameof(StartOfFileName);

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
        @"(^|[.\-_])(\d{3,4}p|4k|[xh]\.?26[45]|HEVC|AVC|BluRay|BDRip|WEB|WEBRip|WEB-?DL|HDTV|DVDRip|REMUX|UHD)([.\-_]|$)"
        + @"|(^|[.\-_])(German|English|Deutsch|MULTi|DL|AC3|DTS|EAC3|DDP?5|TrueHD|Atmos|HDR|DV|SDR)([.\-_]|$)"
        + @"|(^|[.\-_])(iNTERNAL|REPACK|PROPER|UNRATED|EXTENDED|COMPLETE|UNCUT|ANiME|RETAIL)([.\-_]|$)"
        + @"|S\d{1,2}E\d{1,3}|(^|[.\-_])E\d{2,4}([.\-_]|$)|(^|[.\-_])OVA\d*([.\-_]|$)|-[A-Za-z0-9]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// The characters at which Jellyfin cuts a file name short when it has no better title.
    /// </summary>
    /// <remarks>
    /// Copied, not reasoned: the separator class of the first <c>CleanStrings</c> pattern,
    /// <c>[ _\,\.\(\)\[\]\-]</c>, in <c>Emby.Naming/Common/NamingOptions.cs</c> line 154 at tag
    /// <c>v12.1</c> - line 152 is only <c>CleanStrings =</c>. See <see cref="StartsTheLeaf"/>.
    /// </remarks>
    private static readonly char[] CutAt = [' ', '_', ',', '.', '(', ')', '[', ']', '-'];

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

        // Halves B and C behind their shared gate, decided in ONE call as upstream does, so the
        // vectors test the gate too. Only the reason tells the two apart.
        if (IsNamedAfterItsFile(name, leaf, identified))
        {
            reasons.Add(IsTheLeaf(name, leaf) ? SameAsFileName : StartOfFileName);
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
    /// Half A: the title reads as a file name, dotted or hyphenated.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <returns>True when it does.</returns>
    /// <remarks>
    /// <para>
    /// The "length >= 2" floor is what spares acronyms: <c>S.W.A.T.</c> has four dots and no
    /// piece of two characters, so it never reaches three.
    /// </para>
    /// <para>
    /// <b>The hyphen branch was added upstream on 2026-09-03 and is ported here verbatim.</b>
    /// A hyphen-separated release name - <c>tvr-lots-s02e01</c>, <c>tmsf-highscore-s01e01</c> -
    /// carries no dots at all and was invisible to this rule. It came to light from this side:
    /// <see cref="Models.LayoutFindingKind.EpisodeShapedMovie"/> reported 22 misfiled
    /// documentary episodes and this rule saw only 16 of them, one whole folder missing.
    /// </para>
    /// <para>
    /// <b>On hyphens alone the rule is too coarse, and the extra condition is case.</b>
    /// Measured upstream over 44,528 titles, hyphens add 85 rows of which 19 are real German
    /// episode titles - <c>Gute-Nacht-Geschichten</c>, <c>Kopf-An-Kopf-Rennen</c>,
    /// <c>Papier-Blüten-Träume</c>. What separates the two sets without exception is that a
    /// German compound is capitalised and a scene release is not. With the lower-case
    /// condition the same measurement gives 66 rows, every one a release name, and leaves the
    /// dotted findings untouched. The accepted false positive is <c>ai-mai-mi</c>, a
    /// lower-case title that really does carry hyphens.
    /// </para>
    /// <para>
    /// <b>Case-sensitive on the hyphen half only.</b> A dotted release name is usually
    /// capitalised (<c>Mr.Robot.S03E02.German…</c>), so the same condition on the dotted half
    /// would empty the finding entirely.
    /// </para>
    /// <para>
    /// <b>The case test stops at the first dot</b> - ported verbatim from upstream on 2026-09-23.
    /// Some groups name their files <c>group-show-s01e04.Title</c>: the release part is
    /// hyphenated and lower case, the episode title sits behind a dot and is capitalised. With a
    /// one-word title the name has a single dot, so the dotted half does not see it, and the
    /// hyphen half used to test the WHOLE name and lost it to the title's capital letter.
    /// Storage Wars Canada showed it upstream: 22 of 36 such episodes found, the 14 with a
    /// one-word title (<c>…s01e04.Ueberlistet</c>) not. Measured there over 45,779 titles,
    /// stopping at the dot adds 12 rows and every one is a release name
    /// (<c>tvp-lucifer-s01e01.Pilot</c>, <c>tvr-divorce-s02e04.Ohio</c>) - no season, and
    /// nothing lost. A German compound is capitalised BEFORE any dot, so it stays out as before
    /// (<c>Kopf-An-Kopf-Rennen.Teil.2</c>).
    /// </para>
    /// <para>
    /// Public because <see cref="Models.LayoutFindingKind.PerEpisodeFolder"/> asks the same
    /// question of a season's name. Sharing the predicate is the point: two rules that look
    /// alike are two rules that drift apart - and it means a change here moves that finding
    /// too, which is intended rather than a side effect.
    /// </para>
    /// </remarks>
    public static bool LooksLikeAFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        // Dots OR hyphens, and the hyphen half additionally demands lower case up to the first
        // dot - see the remarks above for why that one extra condition is what makes it usable,
        // and why it stops at the dot.
        return Separated(name, '.')
               || (Separated(name, '-') && !name.Split('.')[0].Any(char.IsUpper));
    }

    /// <summary>
    /// Whether a name falls apart into release-name pieces on one separator.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="separator">The character to split on.</param>
    /// <returns>True for two or more separators and three or more pieces that are words.</returns>
    private static bool Separated(string name, char separator)
    {
        var count = name.Length - name.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal).Length;
        if (count < 2)
        {
            return false;
        }

        return name.Split(separator).Count(piece => piece.Length >= 2) >= 3;
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
    /// Whether an unidentified entry's title was taken from its file or folder name.
    /// </summary>
    /// <param name="name">The title.</param>
    /// <param name="leaf">The last part of its path.</param>
    /// <param name="identified">Whether the item carries provider ids.</param>
    /// <returns>True when the title is the leaf, or the start of it.</returns>
    /// <remarks>
    /// Ported verbatim from upstream (3.0.0, 2026-09-24). Halves B and C behind the one
    /// condition they share: nothing identified the entry. For an identified one both describe
    /// a well kept library - measured upstream, 727 identified entries have a title that starts
    /// their file name (<c>A Quiet Place</c> in <c>A Quiet Place (2018).mkv</c>), and not one of
    /// them is a finding. One method rather than two conditions in the caller so the acceptance
    /// check and this port can hold the whole rule, gate included, against the same vectors.
    /// </remarks>
    internal static bool IsNamedAfterItsFile(string name, string leaf, bool identified)
        => !identified && (IsTheLeaf(name, leaf) || StartsTheLeaf(name, leaf));

    /// <summary>Whether the title is the start of its file or folder name.</summary>
    /// <param name="name">The title.</param>
    /// <param name="leaf">The last part of its path.</param>
    /// <returns>True when the leaf begins with the title and goes on at one of Jellyfin's cuts.</returns>
    /// <remarks>
    /// <para>
    /// Half C. When nothing matches, Jellyfin does not show the whole file name: its first
    /// <c>CleanStrings</c> pattern cuts it before the first release marker - a resolution, a
    /// source, a language, a codec. <c>black-1080p.divorce.s01e01.Der.Scheidungswunsch.mkv</c>
    /// becomes <c>black</c>, the group's prefix; <c>Futurama.1999.S08E11.German…</c> becomes
    /// <c>Futurama</c>, the series name. Neither has the shape of a file name, so half A cannot
    /// see them, and neither is the whole leaf, so half B cannot either.
    /// </para>
    /// <para>
    /// Measured upstream on 2026-09-24 over 45,838 entries: 55 rows neither half found, every
    /// one an unidentified episode - Disenchantment 20, Futurama 29, Archer 3, Time 2,
    /// Detectorists 1 - and not one series, season or film. The leaf has to go on at one of
    /// <see cref="CutAt"/>: <c>Time</c> is not what Jellyfin makes of <c>Timeless.S01E01…</c>.
    /// </para>
    /// <para>
    /// Four of the six <c>CleanStrings</c> patterns (lines 154-159 at <c>v12.1</c>) end at one of
    /// those characters: 154, 155, 158 and 159. The other two are left out on purpose: 156 cuts
    /// before an episode range (<c>E01-E02</c>) at ANY non-word character, 157 drops a leading
    /// <c>[group]</c>, so the title comes from the middle of the leaf. Measured upstream over the
    /// 122 unidentified entries the rule does not find: none of either shape, with the same query
    /// finding the 77 of halves B and C. Raised from this side while reviewing the port.
    /// </para>
    /// </remarks>
    private static bool StartsTheLeaf(string name, string leaf)
        => leaf.Length > name.Length
           && leaf.StartsWith(name, StringComparison.OrdinalIgnoreCase)
           && Array.IndexOf(CutAt, leaf[name.Length]) >= 0;

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
