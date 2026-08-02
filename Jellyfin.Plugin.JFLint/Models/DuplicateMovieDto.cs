using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One movie file sharing an identity with at least one other.
/// </summary>
/// <remarks>
/// Unlike the episode case a finding here is often <b>wanted</b> - 1080p beside 2160p, cut
/// beside uncut. The route reports, it does not accuse; the fields are what let a caller
/// tell a deliberate second copy from a real duplicate, and both from a mis-identification
/// where unrelated films ended up on one provider id.
/// </remarks>
/// <param name="Id">The movie item id.</param>
/// <param name="Name">The movie name.</param>
/// <param name="ProductionYear">The production year.</param>
/// <param name="IdentityKey">
/// Which key grouped this row, prefixed with its source - <c>Tmdb:1892</c>,
/// <c>Imdb:tt0088763</c> or <c>Key:…</c>. Named rather than implied, so a caller can see
/// what a group rests on. A <c>Key:</c> group means the files are already linked as
/// alternate versions of one another; it is not a catch-all for movies without a provider
/// id, since an unlinked movie's presentation key is its own item id and cannot collide.
/// </param>
/// <param name="Path">The file on disk.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Width">Video width in pixels, null when unknown.</param>
/// <param name="Height">Video height in pixels, null when unknown.</param>
/// <param name="PrimaryVersionId">
/// Set when the file is already linked as an alternate version of another - the field that
/// separates settled cases from open ones, and unreachable over the stock API. Reported as
/// a <c>Guid</c> on both Jellyfin lines, although 10.11 still stores it as a string.
/// </param>
public sealed record DuplicateMovieDto(
    Guid Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ProductionYear,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IdentityKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Width,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Height,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? PrimaryVersionId);
