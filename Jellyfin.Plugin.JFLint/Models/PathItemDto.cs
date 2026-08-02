using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One item found at a path. Carries the four fields a caller needs to act on it -
/// deliberately not a <c>BaseItemDto</c>, for the same reason as
/// <see cref="OrphanEpisodeDto"/>.
/// </summary>
/// <param name="Id">The item id, which is what <c>/Items/{id}/Refresh</c> and
/// <c>DELETE /Items/{id}</c> take.</param>
/// <param name="ItemType">The short type name - <c>Movie</c>, <c>Episode</c>,
/// <c>Series</c> and so on. Lets a caller refuse container types before deleting.</param>
/// <param name="Name">The item's name, which after a bad metadata match may have nothing
/// to do with the file name.</param>
/// <param name="Path">The item's path on disk.</param>
public sealed record PathItemDto(
    Guid Id,
    string ItemType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path);
