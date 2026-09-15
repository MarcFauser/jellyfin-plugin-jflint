using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One declared foreign key and how many rows violate it - or one table that declares none at
/// all, and is therefore outside what the check can see.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is one row per declared foreign key, not one per violation.</b> The reference
/// library holds 202,806 violations and nobody can act on a list that long; ten lines naming
/// which relations lost their parent is the thing a caller can read, and the number to look at
/// again after a repair to see whether it worked.
/// </para>
/// <para>
/// <b>Rows with no violations are included, and so are tables that cannot be checked at all.</b>
/// That is the whole point of the shape rather than padding: a check that reports only what it
/// found renders "clean" and "could not look" as the same empty list, which is a guard that
/// cannot fire. <see cref="RowCount"/> separates them - <c>0</c> means checked and clean,
/// <c>null</c> means there was no declared foreign key to check.
/// </para>
/// </remarks>
/// <param name="Table">The child table.</param>
/// <param name="Column">The column holding the reference, from
/// <c>pragma_foreign_key_list</c>. Null when the table declares no foreign key.</param>
/// <param name="Parent">The table it points at. Null likewise.</param>
/// <param name="RowCount">How many rows violate this foreign key. <b>Null is not zero</b>:
/// null means the table declares no foreign key, so nothing was checked, while zero means it
/// was checked and is clean. A caller that renders both as an empty cell throws away the
/// distinction this route exists to make.</param>
/// <param name="DeclaredForeignKeys">How many foreign keys the table declares in total. Zero
/// says the table is outside the check by construction - any dangling reference it holds is
/// invisible here however broken, because SQLite was never told to enforce it.</param>
/// <param name="TableRows">How many rows the table holds, which is what makes
/// <see cref="RowCount"/> readable. <b>A zero over an empty table is not a clean bill of
/// health</b> - an empty table cannot produce a violation, so its zero says nothing about
/// anything, while a zero over a quarter of a million rows is a real finding. Without this
/// field the two render identically, which is the same mistake as conflating null with zero,
/// one level down. Reported by the first caller, who found two such relations on the reference
/// server.</param>
public sealed record OrphanRowFindingDto(
    string Table,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Column,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Parent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RowCount,
    int DeclaredForeignKeys,
    long TableRows);
