using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One release folder that gives every episode its own directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>This finding has no item id, and that is the shape rather than an omission.</b> Its
/// subject is a folder on disk, derived from the paths of the episodes beneath it - Jellyfin
/// may have resolved that folder into an item, several items or none at all, and the finding
/// holds either way. The neighbouring <c>PerEpisodeFolder</c> kind reports the other end of the
/// same phenomenon and does carry an id, because its subject is a <c>Season</c> that Jellyfin
/// created. Forcing this into <c>LayoutFindingDto</c> would mean a mandatory field that is
/// structurally always empty, which reads as a missing value rather than as an absent question.
/// </para>
/// <para>
/// <b>Why it is called a release and not a season.</b> The criterion does not constrain what
/// the parent folder is - on the reference library one of them holds 252 per-episode
/// directories, which is no season. Series folder, season folder or a multi-season release all
/// qualify, so the name says the one thing that is always true.
/// </para>
/// </remarks>
/// <param name="Folder">The release folder, in the spelling a materialised item reports. The
/// database half expands the stored form before the grouping, not after, so both halves group
/// identical strings - see <see cref="StoredPath"/>.</param>
/// <param name="SeriesName">The series every episode beneath it belongs to, or <b>null</b> when
/// it holds more than one. Null is a warning rather than a fallback: after the library-root bar
/// no release on the reference library holds several series, so a null says the folder
/// structure is not what the rule assumes.</param>
/// <param name="FolderCount">How many per-episode folders the release holds - the number the
/// caller prints, and the size of the flattening job.
/// <para>
/// <b>There is deliberately no second count of episodes.</b> Measured on the reference library
/// once the library root is excluded: 3,172 per-episode folders against 3,172 episodes, exactly
/// equal. The two differ only through folders holding more than one episode, which are not
/// per-episode folders and do not enter the count at all. Publishing both would advertise a
/// distinction that does not exist.
/// </para></param>
public sealed record UnflattenedReleaseDto(
    string Folder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesName,
    int FolderCount);
