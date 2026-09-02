using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Decides whether an item Jellyfin resolved as a film is really one episode of a series.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a stricter <see cref="FileNameTitleRule"/>.</b> That one asks whether a
/// title is nothing but a file name; this one asks whether the <b>type</b> is wrong. The two
/// overlap heavily - on the reference library 16 of 22 rows are reported by both - and they
/// want different repairs: a name against a library move. Neither subsumes the other, and
/// merging them would produce a check that can only recommend one of the two.
/// </para>
/// <para>
/// <b>Why it exists at all.</b> A mixed library - one with no <c>CollectionType</c> - has
/// Jellyfin decide per folder whether it holds a film or a series, and for a season folder it
/// sometimes decides "film". Every episode then becomes its own movie. That was visible until
/// now only where those episodes happened to resolve to one provider id and so appeared as a
/// duplicate group; a fourth folder on the reference library produced no duplicate at all and
/// was invisible. A check that finds a fault only where it happens to trip another check is
/// not a check on that fault.
/// </para>
/// <para>
/// <b>The judgement is on the file name, not the folder.</b> Measured on 2368 films: a folder
/// criterion (<c>Sxx</c> without an episode number, which is what a season folder carries)
/// would have added exactly two rows, and both are false - <c>Gintama.S00.The.Movie.1</c> and
/// its sequel are genuine films whose release group used <c>S00</c> as a specials marker. Two
/// wrong and none right is not a trade; the folder is worth reporting as context and never as
/// the trigger.
/// </para>
/// </remarks>
internal static class EpisodeShapedMovieRule
{
    /// <summary>
    /// The finding is reported because the file name carries a season and episode number.
    /// </summary>
    public const string EpisodeMarkerInFileName = nameof(EpisodeMarkerInFileName);

    // A season/episode marker in the file name. Deliberately NOT the S\d{1,2}E\d{1,3} token
    // from FileNameTitleRule.Evidence, and the difference is the point: there it is one of
    // many alternatives establishing "this is a release name", so reach costs nothing; here it
    // is the sole criterion, so precision outweighs reach. This form additionally accepts the
    // separated conventions S01.E01 and S01-E01, which that token misses, and refuses a match
    // embedded without boundaries (abcS01E01xyz), which is where false positives begin.
    // Measured on 2368 films: the two agree exactly, 22 rows either way, so adopting the
    // stricter one costs nothing today and covers more tomorrow.
    private static readonly Regex EpisodeMarker = new(
        @"(^|[._ \-])s\d{1,2}[._ \-]?e\d{1,3}([._ \-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Decides whether a film's path names an episode, and why.
    /// </summary>
    /// <param name="path">The item's path, which may be null for a virtual row.</param>
    /// <returns>The reasons it is reported, empty when it is not a finding.</returns>
    public static IReadOnlyList<string> Evaluate(string? path)
    {
        var file = FileName(path);
        if (file.Length == 0)
        {
            return Array.Empty<string>();
        }

        var match = EpisodeMarker.Match(file);
        return match.Success
            ? new[] { EpisodeMarkerInFileName, "marker " + match.Value.Trim('.', '_', ' ', '-') }
            : Array.Empty<string>();
    }

    /// <summary>
    /// The last path segment without its extension, splitting on either separator.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The bare file name, or empty.</returns>
    /// <remarks>
    /// Written here rather than borrowed from <see cref="FileNameTitleRule"/>, whose equivalent
    /// is private and whose file is a verbatim port that is not edited on this side. Splitting
    /// a path is not the judgement, so two implementations of it carry no risk of the two rules
    /// drifting to different verdicts.
    /// <para>
    /// <see cref="System.IO.Path"/> is not used on purpose: it splits on the separator of the
    /// host it runs on, and these paths come from a Linux server read by whatever the plugin
    /// happens to run on. Both separators are always tested.
    /// </para>
    /// </remarks>
    private static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var leaf = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;

        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }
}
