using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One provider id that could not identify anything.
/// </summary>
/// <remarks>
/// <b>One row per id, not per item.</b> An item may carry several implausible ids - on the
/// reference library every affected folder row has both an AniDb and an AniList sentinel - and
/// a caller that wants to remove one provider but keep another needs them apart. Rolling up to
/// the item would also hide the case the whole finding exists for: a single bad value in a
/// batch of 518 is what tipped SkipMe.db over, and "this series has a problem" does not say
/// which field to touch.
/// </remarks>
public sealed record ProviderIdFindingDto
{
    /// <summary>
    /// Gets the id of the item carrying it. This is the <b>row</b> id and it is what
    /// <c>RemoveProviderId</c> expects.
    /// </summary>
    /// <remarks>
    /// On Jellyfin 12 the merged view of a series shows one item where the database holds one
    /// row per release folder - measured on the reference library, 13 series against 85 rows.
    /// The ids here are the rows, because that is where the values sit; passing the merged ids
    /// instead would repair 13 of 85 and leave the fault in place.
    /// </remarks>
    public required Guid ItemId { get; init; }

    /// <summary>
    /// Gets the short type name - <c>Movie</c>, <c>Series</c> or <c>Episode</c>.
    /// </summary>
    public required string ItemType { get; init; }

    /// <summary>
    /// Gets the provider name exactly as it is stored, which is the spelling
    /// <c>RemoveProviderId</c> will match case-insensitively.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Gets the stored value.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Gets why it cannot identify anything.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the item's name.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; init; }

    /// <summary>
    /// Gets the series the item sits under, null for a series or a film.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SeriesName { get; init; }

    /// <summary>
    /// Gets the item's path, which is what tells two rows of one merged series apart.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Path { get; init; }
}
