using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One file covering an episode number that is covered more than once.
/// </summary>
/// <remarks>
/// One row per <b>file</b>, not per number, so a caller can group them and show the copies
/// side by side. A server-wide run therefore returns roughly twice as many rows as there
/// are affected numbers.
/// </remarks>
/// <param name="Id">The episode item id.</param>
/// <param name="SeriesName">The series name, for display only - never as a key.</param>
/// <param name="SeriesKey">
/// <c>SeriesPresentationUniqueKey</c>, the key the rows were grouped on and the one the
/// caller should group on too. Not reachable over the stock API, which is why this route
/// exists.
/// </param>
/// <param name="SeasonNumber">The season number.</param>
/// <param name="EpisodeNumber">The episode number.</param>
/// <param name="Name">The episode title.</param>
/// <param name="Path">The file on disk.</param>
/// <param name="Size">File size in bytes - together with the resolution this is what
/// decides which copy to keep.</param>
/// <param name="Width">Video width in pixels, null when unknown.</param>
/// <param name="Height">Video height in pixels, null when unknown.</param>
/// <param name="PrimaryVersionId">
/// Set when the item is linked as an alternate version of another. Reported as a <c>Guid</c>
/// on both Jellyfin lines, although 10.11 still stores it as a string: the field is a Guid in
/// v12, and the payload follows where the schema is going rather than where it has been.
/// <para>
/// <b>This used to read "a settled decision rather than an open one", and on v12 that is
/// false.</b> It was true of 10.11, where only <c>POST /Videos/MergeVersions</c> ever set the
/// column, so a link meant somebody had decided. Jellyfin 12 merges alternate versions of an
/// episode <i>by itself</i>, and nobody decided anything. Measured on the reference library:
/// of 647 rows, 23 carry a link and every one of them was made automatically - among them four
/// Remington Steele seasons and JAG, where genuinely different specials were merged because
/// they all parse as episode 0. Reading a link as "already handled" throws those away, and they
/// are the rows worth looking at.
/// </para>
/// <para>
/// So the field says <i>how Jellyfin has grouped these files</i>, not whether the finding is
/// closed. On v12 a set link is a reason to look, not a reason to skip.
/// </para>
/// </param>
public sealed record DuplicateEpisodeDto(
    Guid Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeasonNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? EpisodeNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Width,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Height,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? PrimaryVersionId);
