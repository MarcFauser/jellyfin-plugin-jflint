using System;

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
/// whether a group rests on a provider id or on the weaker fallback.
/// </param>
/// <param name="Path">The file on disk.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Width">Video width in pixels, null when unknown.</param>
/// <param name="Height">Video height in pixels, null when unknown.</param>
/// <param name="PrimaryVersionId">
/// Set when the file is already linked as an alternate version of another - the field that
/// separates settled cases from open ones, and unreachable over the stock API. A string
/// rather than a Guid because that is what both the column and the object model hold.
/// </param>
public sealed record DuplicateMovieDto(
    Guid Id,
    string? Name,
    int? ProductionYear,
    string? IdentityKey,
    string? Path,
    long? Size,
    int? Width,
    int? Height,
    string? PrimaryVersionId);
