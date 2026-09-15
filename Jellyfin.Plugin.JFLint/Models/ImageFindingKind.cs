namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// The image-row findings this plugin can report. Each name is the value of
/// <see cref="ImageRowFindingDto.Kind"/>, so the constant and the string cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <c>const string</c> rather than an enum, exactly as
/// <see cref="LayoutFindingKind"/> is. A caller groups its display by these values and keys its
/// language files off them, so the wire value has to be the stable thing; an enum would
/// serialise as an integer by default and a renumbering would silently reshuffle a caller's
/// groups without breaking anything visible.
/// </para>
/// <para>
/// These are a separate set from <see cref="LayoutFindingKind"/> rather than three more entries
/// in it, because they are findings about a different subject. A layout finding names an item
/// whose place in the library is wrong; these name a <b>row</b> in the image table, and several
/// of them can belong to one item.
/// </para>
/// </remarks>
public static class ImageFindingKind
{
    /// <summary>
    /// More than one row in the image table describes the same image of the same item - same
    /// item, same image type, same file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one finding here that is not cosmetic. Every library scan stats every image row, so
    /// duplicated rows multiply the file system round trips of every future scan; a reported
    /// case upstream reached 262,144 rows for four real images on one movie.
    /// </para>
    /// <para>
    /// Nothing prevents it at the schema level on either line, so the check does not have a
    /// shelf life. Both lines index the table and neither index is unique: 10.11 carries the
    /// single-column <c>IX_BaseItemImageInfos_ItemId</c> that EF's foreign-key convention
    /// produces, and Jellyfin 12 drops that one - migration
    /// <c>20260206224832_IndexOptimizations</c> - in favour of a composite
    /// <c>(ItemId, ImageType)</c> declared in <c>BaseItemImageInfoConfiguration</c>, which calls
    /// <c>HasIndex</c> without <c>IsUnique</c>.
    /// </para>
    /// </remarks>
    public const string ImageRowDuplicated = nameof(ImageRowDuplicated);

    /// <summary>
    /// An image row whose stored width or height is zero - the dimensions were never read.
    /// </summary>
    /// <remarks>
    /// The columns are non-nullable <c>int</c> on both lines, so zero is the only value an
    /// unknown dimension can take; there is no null to tell "unknown" from "really zero".
    /// </remarks>
    public const string ImageWithoutDimensions = nameof(ImageWithoutDimensions);

    /// <summary>
    /// An image row with no blurhash stored - the column is null, or an empty blob.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The empty blob is not a pedantic addition, it is the half a hand-written query
    /// misses.</b> The column is <c>byte[]</c>, not text - measured out of the shipped
    /// assemblies on both lines - and <c>BaseItemRepository</c> writes
    /// <c>Encoding.UTF8.GetBytes(BlurHash)</c>, so a blurhash of <c>""</c> is stored as a
    /// zero-length blob rather than as null.
    /// </para>
    /// <para>
    /// In SQLite a blob never compares equal to a text value, so <c>WHERE Blurhash = ''</c>
    /// cannot match even an empty blob. Measured in-memory on sqlite 3.46.1 over three rows
    /// (null, empty blob, real blurhash): <c>Blurhash = ''</c> returns 0, <c>Blurhash = x''</c>
    /// returns 1, and <c>length(Blurhash) = 0</c> returns 1 - with the control that the same
    /// <c>= ''</c> against a text column does return 1, so it is the storage class and not the
    /// operator. This check uses the length form.
    /// </para>
    /// </remarks>
    public const string ImageWithoutBlurhash = nameof(ImageWithoutBlurhash);
}
