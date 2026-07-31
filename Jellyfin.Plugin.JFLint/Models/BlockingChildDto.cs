using System;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One descendant standing in the way of a removal.
/// </summary>
/// <param name="Id">The descendant's item id.</param>
/// <param name="ItemType">Its short type name.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">
/// Its path, which is the field that separates the two explanations: a blocker without a
/// path is a virtual entry, one with a path is something the caller can go and look at.
/// </param>
public sealed record BlockingChildDto(
    Guid Id,
    string ItemType,
    string? Name,
    string? Path);
