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
}
