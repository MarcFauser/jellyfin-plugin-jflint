using System;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One id that was actually removed.
/// </summary>
/// <remarks>
/// The response lists what changed rather than returning a count, so a caller can verify the
/// outcome against the list it showed the user instead of trusting a number. An item whose id
/// was already absent produces no row here - which is how "nothing matched" and "everything
/// matched" stay distinguishable without a second query.
/// </remarks>
public sealed record RemovedProviderIdDto
{
    /// <summary>
    /// Gets the row the id was removed from.
    /// </summary>
    public required Guid ItemId { get; init; }

    /// <summary>
    /// Gets the provider key as it was stored, not as it was requested.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Gets the value that was removed, so it can be written back if this turns out wrong.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Gets the item's name, for a readable report.
    /// </summary>
    public required string Name { get; init; }
}
