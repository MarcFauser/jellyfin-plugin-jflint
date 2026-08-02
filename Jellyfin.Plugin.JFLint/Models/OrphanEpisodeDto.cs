using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One episode whose season could not be determined. Carries exactly the fields the
/// calling tool displays - deliberately not a <c>BaseItemDto</c>, since building those
/// for the whole library is the cost this plugin exists to avoid.
/// </summary>
/// <param name="Id">The episode's item id.</param>
/// <param name="SeriesId">The id of the series the episode belongs to, if known.</param>
/// <param name="SeriesName">The name of the series the episode belongs to, if known.</param>
/// <param name="IndexNumber">The episode number within its season, if known.</param>
/// <param name="Name">The episode title.</param>
/// <param name="Path">The path of the episode's media file.</param>
public sealed record OrphanEpisodeDto(
    Guid Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? SeriesId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? IndexNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path);
