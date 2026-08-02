using System;

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
/// Set when the item is already linked as an alternate version of another - a settled
/// decision rather than an open one. A string rather than a Guid because that is what both
/// the column and the object model hold.
/// </param>
public sealed record DuplicateEpisodeDto(
    Guid Id,
    string? SeriesName,
    string? SeriesKey,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Name,
    string? Path,
    long? Size,
    int? Width,
    int? Height,
    string? PrimaryVersionId);
