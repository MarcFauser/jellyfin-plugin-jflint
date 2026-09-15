using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One finding about the rows that describe one image of one item.
/// </summary>
/// <remarks>
/// <para>
/// A finding is per <b>image</b>, not per row: the rows of one item, image type and path are
/// collapsed into a single finding carrying <see cref="RowCount"/>. That is not only for
/// brevity - it is what gives the pair a unique key. The object-model half cannot see a row's
/// primary key, because <c>BaseItemRepository</c> drops it when it maps a
/// <c>BaseItemImageInfo</c> into an <c>ItemImageInfo</c>, so without the grouping two findings
/// about two identical rows would tie on every field and the two halves could agree on the set
/// while differing in order. That is the same fault this plugin fixed in its episode and movie
/// sorts, and here it cannot be fixed with a tiebreaker because there is no unique value left
/// to break the tie with.
/// </para>
/// <para>
/// Every nullable member carries <c>JsonIgnoreCondition.Never</c>, like every other DTO here:
/// the server serialises with <c>WhenWritingNull</c>, so without it a null field would be
/// absent from the payload rather than present and null, and a caller under a strict mode
/// throws on a property that is not there.
/// </para>
/// </remarks>
/// <param name="ItemId">The item the image belongs to.</param>
/// <param name="ItemName">The item's name.</param>
/// <param name="ItemType">The item's short type name - <c>Movie</c>, <c>Episode</c>,
/// <c>Person</c> and so on. Not restricted to video: most rows in the table belong to people
/// and to metadata artwork.</param>
/// <param name="ImageType">Which image of the item this is - <c>Primary</c>, <c>Backdrop</c>,
/// <c>Logo</c> and so on. Spelled with <c>MediaBrowser.Model.Entities.ImageType</c> on both
/// halves, which is the cast Jellyfin itself applies to the stored value, so the two cannot
/// disagree on spelling by accident.</param>
/// <param name="Path">The file the rows point at, in the spelling a materialised item reports.
/// The database half expands the stored form before reporting it - see
/// <see cref="StoredPath"/>.</param>
/// <param name="RowCount">How many rows of this image the finding covers. For
/// <see cref="ImageFindingKind.ImageRowDuplicated"/> that is every row describing the image and
/// is always greater than one; for the other kinds it is how many of those rows carry the
/// flaw, which is one unless the image is also duplicated.</param>
/// <param name="Kind">Which check produced this row, from <see cref="ImageFindingKind"/>.</param>
public sealed record ImageRowFindingDto(
    Guid ItemId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ItemName,
    string ItemType,
    string ImageType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    int RowCount,
    string Kind);
