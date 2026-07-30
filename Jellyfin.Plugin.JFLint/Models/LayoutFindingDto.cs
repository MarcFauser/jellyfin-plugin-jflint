using System;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One library-layout finding. A single shape serves all five kinds so the calling tool
/// needs one parser for all ten routes; fields that do not apply to a kind stay null and
/// are dropped from the JSON by the server's serializer, which is configured with
/// <c>DefaultIgnoreCondition = WhenWritingNull</c>.
/// </summary>
/// <remarks>
/// Deliberately carries no prose and no numbers inside strings: every sentence the user
/// sees is composed by the caller from its own language files, and a formatted number
/// would have to survive a culture-dependent parse on the way back.
/// </remarks>
public sealed record LayoutFindingDto
{
    /// <summary>
    /// Gets the finding kind - one of the constants in <see cref="LayoutFindingKind"/>.
    /// Repeated in every row so a caller that merges several routes keeps one code path.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the id of the item the finding is about.
    /// </summary>
    public required Guid ItemId { get; init; }

    /// <summary>
    /// Gets the short type name of that item - <c>Series</c>, <c>Season</c> or
    /// <c>Episode</c>.
    /// </summary>
    public required string ItemType { get; init; }

    /// <summary>
    /// Gets the item's name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the name of the series the item sits under. Null for a finding about a series
    /// itself, where <see cref="Name"/> already carries it.
    /// </summary>
    public string? SeriesName { get; init; }

    /// <summary>
    /// Gets the item's path on disk.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the season number. Filled for
    /// <see cref="LayoutFindingKind.DuplicateSeasonNumber"/>.
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>
    /// Gets how many season rows share that number. Filled for
    /// <see cref="LayoutFindingKind.DuplicateSeasonNumber"/>.
    /// </summary>
    public int? GroupSize { get; init; }

    /// <summary>
    /// Gets the episode rows beneath the series, real and virtual together. Filled for
    /// <see cref="LayoutFindingKind.SeriesWithoutFiles"/>, where it is what makes the row
    /// readable: a high count with no playable file means the provider knows the season
    /// and Jellyfin read none of it.
    /// </summary>
    public int? EpisodeRowCount { get; init; }

    /// <summary>
    /// Gets which link is dangling - <c>SeriesId</c>, <c>SeasonId</c> or <c>ParentId</c>.
    /// Filled for <see cref="LayoutFindingKind.OrphanedItem"/>.
    /// </summary>
    public string? DanglingLink { get; init; }

    /// <summary>
    /// Gets the id that link points at. Filled for
    /// <see cref="LayoutFindingKind.OrphanedItem"/>.
    /// </summary>
    public Guid? DanglingId { get; init; }
}
