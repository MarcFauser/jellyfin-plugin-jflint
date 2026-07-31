using System.Collections.Generic;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// Why a removal was refused: how many descendants are in the way, and a sample of them.
/// </summary>
/// <remarks>
/// A bare count is a dead end for a caller - it can report "1 failed" and nothing more.
/// Naming the blockers turns the refusal into a diagnosis, which is the only reason this
/// type exists.
/// </remarks>
/// <param name="Remaining">The exact number of descendants, however many are sampled.</param>
/// <param name="Sample">Up to twenty of them.</param>
public sealed record DeleteConflictDto(
    int Remaining,
    IReadOnlyList<BlockingChildDto> Sample);
