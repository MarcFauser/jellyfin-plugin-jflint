namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// The library-layout findings this plugin can report. Each name is both the route
/// segment and the value of <see cref="LayoutFindingDto.Kind"/>, so the two cannot drift
/// apart.
/// </summary>
public static class LayoutFindingKind
{
    /// <summary>
    /// A season folder Jellyfin could not read a number from, but which does hold files -
    /// typically a per-episode release folder mistaken for a season.
    /// </summary>
    public const string PhantomSeason = nameof(PhantomSeason);

    /// <summary>
    /// A season folder holding no playable episode at all, whatever its number.
    /// </summary>
    public const string SeasonFolderWithoutVideo = nameof(SeasonFolderWithoutVideo);

    /// <summary>
    /// A season sharing its number with another season of the same series.
    /// </summary>
    public const string DuplicateSeasonNumber = nameof(DuplicateSeasonNumber);

    /// <summary>
    /// A series folder that yielded no playable episode - the files are on disk but
    /// Jellyfin read none of them.
    /// </summary>
    public const string SeriesWithoutFiles = nameof(SeriesWithoutFiles);

    /// <summary>
    /// A season or episode pointing at a series, season or parent row that no longer
    /// exists.
    /// </summary>
    public const string OrphanedItem = nameof(OrphanedItem);

    /// <summary>
    /// An entry whose title is nothing but the file or folder it came from.
    /// </summary>
    /// <remarks>
    /// The only kind that also covers <c>Movie</c>, and the only one whose rows are not
    /// necessarily broken - a badly named entry may be matched perfectly well. The two
    /// groups a caller shows are told apart by
    /// <see cref="LayoutFindingDto.HasProviderIds"/>: without an id the match failed, with
    /// one only the name is wrong.
    /// </remarks>
    public const string FileNameTitle = nameof(FileNameTitle);

    /// <summary>
    /// A season that is really one episode's own folder - a release that gives every
    /// episode a directory, which Jellyfin resolves as a season each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A layout fault rather than a metadata one: the series shows twenty seasons of one
    /// episode, and nothing but flattening the folders on disk repairs it.
    /// </para>
    /// <para>
    /// <b>Its rows are also <see cref="FileNameTitle"/> findings</b>, necessarily - a
    /// per-episode folder produces a season whose title is a file name. That is not double
    /// counting to be filtered out: the two answer different questions, "this title is
    /// wrong" against "this layout is wrong", and they want different repairs.
    /// </para>
    /// </remarks>
    public const string PerEpisodeFolder = nameof(PerEpisodeFolder);
}
