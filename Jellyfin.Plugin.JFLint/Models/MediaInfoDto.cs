using System;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// The video properties of one item, including the two that <c>BaseItemDto</c> does not carry.
/// </summary>
/// <remarks>
/// <c>VideoRange</c> and <c>VideoRangeType</c> are absent from <c>BaseItemDto</c> - measured
/// against the running OpenAPI, which lists 153 properties on it and none of them these two -
/// so the only way to read them through the stock API is <c>Fields=MediaStreams</c>, which
/// ships every stream of every item. Measured on the reference library: 76 s and 117 MB for
/// the episodes alone.
/// </remarks>
/// <param name="Id">The item id.</param>
/// <param name="ItemType">The short type name, <c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Name">The item's name.</param>
/// <param name="SeriesName">The series name, null for a movie.</param>
/// <param name="Path">The item's path on disk.</param>
/// <param name="Width">Width in pixels, as the item carries it.</param>
/// <param name="Height">Height in pixels, as the item carries it.</param>
/// <param name="VideoRange">SDR, HDR or unknown, derived by Jellyfin's own
/// <c>MediaStream.GetVideoColorRange()</c>.</param>
/// <param name="VideoRangeType">The finer classification - HDR10, HLG, the Dolby Vision
/// variants - from the same call.</param>
public sealed record MediaInfoDto(
    Guid Id,
    string ItemType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Width,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Height,
    VideoRange VideoRange,
    VideoRangeType VideoRangeType);
