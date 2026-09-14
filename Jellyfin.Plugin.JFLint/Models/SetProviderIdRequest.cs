using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// Which provider to set on which rows, and to what.
/// </summary>
/// <remarks>
/// <b>One provider per call, unlike <see cref="RemoveProviderIdRequest"/>.</b> Removing several
/// keys at once is one decision applied repeatedly; setting several to one value is almost
/// always a mistake, because the value that makes sense for <c>AniList</c> is rarely the value
/// that makes sense for <c>Tvdb</c>. The shape of the request says so.
/// </remarks>
public sealed record SetProviderIdRequest
{
    /// <summary>
    /// Gets the row ids to touch. These are database rows, not the merged view - see
    /// <see cref="ProviderIdFindingDto.ItemId"/>.
    /// </summary>
    public required IReadOnlyList<Guid> ItemIds { get; init; }

    /// <summary>
    /// Gets the provider key, matched case-insensitively against what is stored and written
    /// verbatim when the key is new.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Gets the value to write. Null or blank means <b>remove the key</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blank cannot mean "store an empty id", because Jellyfin will not keep one:
    /// <c>TrySetProviderId</c> returns false on a blank value, <c>SetProviderId</c> throws, and
    /// <c>IsValidProviderId</c> rejects it - so a merge would drop it again. Treating blank as
    /// "remove" is therefore the only reading that corresponds to something the server can
    /// actually hold.
    /// </para>
    /// <para>
    /// A non-numeric value such as <c>none</c> is kept: <c>IsValidProviderId</c> passes any
    /// non-blank value for a provider with no registered validator, and only Imdb, Tmdb,
    /// TmdbCollection, AudioDb and MusicBrainz have one. That is what lets a suppression
    /// sentinel survive a later refresh.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Value { get; init; }
}
