using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JFLint.Models;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Finds release folders that give every episode its own directory - the layout that has to be
/// flattened before Jellyfin can read the season.
/// </summary>
/// <remarks>
/// <para>
/// <b>The criterion is the calling tool's, taken verbatim so the route and that tool's own
/// fallback stay comparable.</b> Group episodes by their parent folder; a folder holding
/// exactly one episode is a per-episode folder; group those by <i>their</i> parent; a parent
/// holding at least <see cref="DefaultMinFolders"/> of them is a release. The third step is
/// what keeps an ordinary season that happens to hold one episode out of the list - it has no
/// siblings of its own kind.
/// </para>
/// <para>
/// <b>Why it is a rule class and not two queries.</b> Both halves of the pair call this, so
/// they cannot drift to different verdicts. A pair exists to detect drift, not to create it,
/// and two hand-written implementations of one criterion are the most reliable way to create
/// some.
/// </para>
/// <para>
/// <b>A pattern on the path is NOT the criterion, and that was measured rather than argued.</b>
/// <c>SxxExx</c> anywhere in the path matches 26,194 of 26,884 episodes - almost everything,
/// because every episode <i>file</i> carries it. Applied to the folder name it is good but not
/// exact: 3,163 of 3,191 per-episode folders, 99.1 %, with one false positive. It is still the
/// wrong instrument, because the case that actually breaks Jellyfin's season detection is the
/// folder named <c>…E01.…</c> <b>without</b> a season, and a season/episode pattern is blind to
/// exactly that class. It measures zero of them today only because this library currently has
/// none. A <c>LIKE</c> would also have to be applied on one half only, which is how a pair
/// stops being a control - the lesson from 11.18.0.0.
/// </para>
/// </remarks>
internal static class UnflattenedReleaseRule
{
    /// <summary>
    /// How many per-episode folders a parent needs before it counts as a release.
    /// </summary>
    /// <remarks>
    /// Measured on the reference library: at three the criterion reports 216 releases, at two
    /// it reports 221. Five rows, so the threshold is a blunt instrument rather than the
    /// sensitive knob it looks like - the calling tool expected more and said so.
    /// </remarks>
    public const int DefaultMinFolders = 3;

    /// <summary>
    /// The lowest threshold that still means anything.
    /// </summary>
    /// <remarks>
    /// At one, every folder holding a single episode would make its parent a release, which
    /// reports an ordinary season as a finding. That is not a stricter or looser answer to the
    /// question, it is a different question, so it is refused rather than allowed.
    /// </remarks>
    public const int MinimumThreshold = 2;

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Finds the release folders among a set of episode paths.
    /// </summary>
    /// <param name="episodes">Every episode with a path, and the series it belongs to.</param>
    /// <param name="libraryRoots">The configured library locations, which can never be a
    /// release however well they fit the shape - see the remarks.</param>
    /// <param name="minFolders">How many per-episode folders make a release.</param>
    /// <returns>One row per release folder, ordered identically whoever called it.</returns>
    /// <remarks>
    /// <para>
    /// <b>The library-root bar is not tidiness, it removes a real false positive.</b> Measured
    /// by the calling tool: of 216 releases on the reference library exactly one holds episodes
    /// of several series, and it is <c>…/Series/1080p</c> - the configured library root itself.
    /// It qualifies because three release folders directly beneath it happen to contain a single
    /// episode each, so the tab would offer to flatten a whole library. The bar is exact rather
    /// than a heuristic: a candidate is refused when it <b>is</b> a configured location, not
    /// when single-episode folders happen to be a minority of its children.
    /// </para>
    /// <para>
    /// A folder <i>above</i> a configured root is not excluded, and that is deliberate rather
    /// than overlooked: to qualify it would need at least <paramref name="minFolders"/> of its
    /// own children to hold exactly one episode, and a library root holds far more, so it
    /// cannot be one of them. If that ever changes it will show up as a finding naming a path
    /// outside the library, which is recognisable.
    /// </para>
    /// <para>
    /// <b>Ordinal comparison throughout</b>, because SQLite's default collation is BINARY and
    /// the server's filesystem is case-sensitive. The two halves have to agree, and agreeing on
    /// the wrong comparison would be worse than disagreeing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<UnflattenedReleaseDto> Evaluate(
        IEnumerable<(string Path, string? SeriesName)> episodes,
        IEnumerable<string> libraryRoots,
        int minFolders)
    {
        var roots = new HashSet<string>(libraryRoots.Select(Normalise), StringComparer.Ordinal);

        // Step 1 and 2: a folder holding exactly one episode. The series name travels with it,
        // because after the grouping there is no way back to the episode it came from.
        var perEpisodeFolders = episodes
            .Where(episode => !string.IsNullOrEmpty(episode.Path))
            .GroupBy(episode => ParentOf(episode.Path), StringComparer.Ordinal)
            .Where(group => group.Key is not null && group.Count() == 1)
            .Select(group => (Folder: group.Key!, group.First().SeriesName))
            .ToList();

        // Step 3 and 4: their parent, and how many of them it holds.
        var releases = perEpisodeFolders
            .GroupBy(folder => ParentOf(folder.Folder), StringComparer.Ordinal)
            .Where(group => group.Key is not null
                            && group.Count() >= minFolders
                            && !roots.Contains(group.Key!))
            .Select(group => new UnflattenedReleaseDto(
                group.Key!,
                SingleSeriesName(group.Select(folder => folder.SeriesName)),
                group.Count()));

        return Sorted(releases);
    }

    /// <summary>
    /// The one series a release belongs to, or null when it holds more than one.
    /// </summary>
    /// <param name="names">The series names of the per-episode folders beneath it.</param>
    /// <returns>The single distinct name, or null.</returns>
    /// <remarks>
    /// <b>Null is a warning here, not a fallback.</b> "Take the first" would have been the
    /// obvious choice and is unusable: the two halves read their rows in different orders, so
    /// the first is not the same row on both sides and the pair would disagree without either
    /// half being wrong. But the better reason is the calling tool's measurement - after the
    /// library-root bar, <b>no</b> release on the reference library holds more than one series.
    /// So a null arriving here says the folder structure is not what this rule assumes, which
    /// is worth seeing rather than papering over.
    /// </remarks>
    private static string? SingleSeriesName(IEnumerable<string?> names)
    {
        var distinct = names
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// The folder a path sits in.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The parent, or null when there is none worth naming.</returns>
    /// <remarks>
    /// <see cref="System.IO.Path"/> is not used on purpose, for the same reason as in
    /// <see cref="EpisodeShapedMovieRule"/>: it splits on the separator of the host it runs on,
    /// and these paths come from a Linux server read by whatever the plugin happens to run on.
    /// Both separators are always tested.
    /// <para>
    /// A separator at position zero yields null rather than an empty string - that is the
    /// filesystem root, and reporting it as a release folder would be the same class of
    /// nonsense the library-root bar exists to prevent.
    /// </para>
    /// </remarks>
    private static string? ParentOf(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut > 0 ? trimmed[..cut] : null;
    }

    /// <summary>
    /// Strips a trailing separator so a configured location compares equal to a derived parent.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The path without trailing separators.</returns>
    private static string Normalise(string path) => path.TrimEnd(Separators);

    /// <summary>
    /// Orders the findings so both halves can be read line by line.
    /// </summary>
    /// <param name="releases">The releases to order.</param>
    /// <returns>The releases by series, then size, then folder.</returns>
    /// <remarks>
    /// Series first because the caller groups by it, size descending within a series because
    /// the list is a worklist, and the folder last as the tiebreaker - it is unique by
    /// construction, one row per parent folder, so the order is total.
    /// </remarks>
    private static List<UnflattenedReleaseDto> Sorted(IEnumerable<UnflattenedReleaseDto> releases)
        => releases
            .OrderBy(release => release.SeriesName is null)
            .ThenBy(release => release.SeriesName, StringComparer.Ordinal)
            .ThenByDescending(release => release.FolderCount)
            .ThenBy(release => release.Folder, StringComparer.Ordinal)
            .ToList();
}
