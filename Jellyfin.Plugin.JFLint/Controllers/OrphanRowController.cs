using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Rows whose parent row is gone, per declared foreign key, plus which tables can be checked
/// at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a wild idea.</b> On the reference library
/// <c>PRAGMA foreign_key_check</c> reports <b>202,806</b> violations across ten tables -
/// 80,986 in <c>PeopleBaseItemMap</c>, 50,639 in <c>MediaStreamInfos</c>, 23,313 in
/// <c>AncestorIds</c>, 15,589 in <c>Chapters</c>, 11,920 in <c>BaseItemImageInfos</c>, and on
/// down - and every one points at <c>BaseItems</c>. That is not a property of the schema: four
/// of those tables can reference a second parent (<c>Peoples</c>, <c>ItemValues</c>,
/// <c>Users</c>), and not one of those sides is violated. Enforcement did not lapse in general;
/// <c>BaseItems</c> rows went missing and left everything behind.
/// </para>
/// <para>
/// <b>It is not a broken cascade.</b> The constraints carry <c>ON DELETE CASCADE</c>, EF emits
/// them by convention for a required navigation, <c>Microsoft.Data.Sqlite</c> turns
/// <c>PRAGMA foreign_keys</c> on by default, and the cascade fires - measured on EF Core 9.0.11
/// and 10.0.11, including through <c>ExecuteDeleteAsync</c>. What produces orphans is a window
/// in which enforcement is off, and that is ordinary rather than exotic: SQLite cannot alter a
/// constraint in place, so a table rebuild runs with foreign keys disabled. Switching them back
/// on does <b>not</b> re-validate what is already stored, so such rows survive silently and
/// forever. That mechanism is measured. <b>Which event on this database took such a window is
/// not</b>, and this route does not claim to know.
/// </para>
/// <para>
/// <b>One row per declared foreign key, not per violation.</b> Nobody can act on 202,806 lines.
/// Ten lines naming which relations lost their parent is what a caller can read, and it is the
/// figure to look at again after a repair.
/// </para>
/// <para>
/// <b>The rows with nothing to report are the ones that make it honest.</b> A foreign key with
/// no violations comes back with <c>RowCount = 0</c>, and a table that declares no foreign key
/// comes back with <c>RowCount = null</c> and <c>DeclaredForeignKeys = 0</c>. Reporting only
/// what was found would render "clean" and "could not look" as the same empty list - a guard
/// that cannot fire. <c>PRAGMA foreign_key_check</c> sees <b>declared</b> foreign keys only, so
/// a table with none can hold any number of dangling guids and this route will never say so.
/// </para>
/// <para>
/// <b>And <c>TableRows</c> is that same argument one level down.</b> An <b>empty</b> table with
/// a declared foreign key also reports <c>RowCount = 0</c> - correctly, and meaninglessly,
/// because an empty table cannot produce a violation. Without the row count it reads exactly
/// like <c>AncestorIds.ParentItemId</c>'s zero, which is a real finding across 251,970 rows.
/// Two of the seventeen relations on the reference server are that shape. Raised by the first
/// caller against this route's own reasoning, which is the best kind of report to get.
/// </para>
/// <para>
/// <b>The complement is <c>OrphanedItem</c></b>, which looks for item rows pointing at vanished
/// items through exactly those undeclared columns - <c>SeriesId</c>, <c>SeasonId</c>,
/// <c>ParentId</c>. Between the two there is still a gap, and naming it is better than implying
/// there is none.
/// </para>
/// <para>
/// <b>No twin, and the reason is stronger than <c>MediaInfoDB</c>'s.</b> That route is alone
/// because <c>MediaStreamQuery.ItemId</c> is a non-nullable <c>Guid</c>, so its twin would be
/// possible and merely far too slow. Here no twin can exist at all: these rows are defined by
/// their parent being gone, so there is nothing for <c>ILibraryManager</c> to return - the
/// object model is item-shaped and these rows have no item. A pair exists so each half checks
/// the other; a half that cannot see the subject by construction would not be a check.
/// </para>
/// <para>
/// Requires elevation like everything else here. The response carries table and column names
/// rather than data, but it describes the shape of the server's database.
/// </para>
/// </remarks>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class OrphanRowController(IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets one row per declared foreign key with its violation count, plus the tables that
    /// declare none.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <returns>One row per declared foreign key, and one per table without any.</returns>
    /// <remarks>
    /// <para>
    /// <b>Raw SQL rather than LINQ, and not by preference.</b> <c>pragma_foreign_key_check</c>
    /// and <c>pragma_foreign_key_list</c> are SQLite table-valued functions; EF Core has no
    /// LINQ surface for them, so there is nothing to translate. Both statements were generated
    /// and <b>executed</b> against SQLite on EF Core 9.0.11 and 10.0.11 before this was
    /// written, on a seeded database carrying all four cases at once - a violated key, a clean
    /// key, a table with no key, and a parent table - because this project has already shipped
    /// one query that translated on one line and threw at runtime on the other.
    /// </para>
    /// <para>
    /// Two statements and a merge in memory rather than one joined query: calling
    /// <c>pragma_foreign_key_check</c> correlated per table would run the whole check once for
    /// every table, which is the same work multiplied by the number of tables.
    /// </para>
    /// <para>
    /// <b>The check does not depend on enforcement being on.</b> Measured: with
    /// <c>PRAGMA foreign_keys = OFF</c> it still reports every violation. So a zero from this
    /// route is a real zero and not "nothing was checked because the connection had it
    /// disabled" - one fewer way for the answer to be quietly empty.
    /// </para>
    /// <para>
    /// Cost scales with the declared relations rather than with the findings: the pragma walks
    /// every child row of every constrained table and looks its parent up. On the reference
    /// database - 970 MB, about a million child rows - it is not instant, and a caller should
    /// treat this as a slow route.
    /// </para>
    /// </remarks>
    [HttpGet("OrphanRowsDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrphanRowFindingDto>>> GetOrphanRowsFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // Grouped by fkid, not only by table: a table may declare several foreign keys onto
            // the same parent, and collapsing them hides which reference actually broke.
            // AncestorIds is exactly that case - it points at BaseItems twice, once for the row's
            // own item and once for the ancestor it names, and those are different findings.
            var violations = await dbContext.Database.SqlQueryRaw<ViolationRow>(
                """
                SELECT c."table" AS "TableName", c.fkid AS "FkId", COUNT(*) AS "Rows"
                FROM pragma_foreign_key_check AS c
                GROUP BY c."table", c.fkid
                """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // LEFT JOIN, so a table with no foreign key still produces a row - that row is the
            // "this cannot be checked" answer, and dropping it is what would turn the route into
            // a guard that cannot fire.
            var declared = await dbContext.Database.SqlQueryRaw<DeclaredRow>(
                """
                SELECT m.name AS "TableName", l.id AS "FkId", l."from" AS "ColumnName", l."table" AS "ParentName"
                FROM sqlite_master AS m
                LEFT JOIN pragma_foreign_key_list(m.name) AS l
                WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
                """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var perTable = declared
                .GroupBy(row => row.TableName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(row => row.FkId is not null), StringComparer.Ordinal);

            var tableRows = await CountRowsAsync(dbContext, perTable.Keys, cancellationToken).ConfigureAwait(false);

            var findings = declared.Select(row => new OrphanRowFindingDto(
                row.TableName,
                row.ColumnName,
                row.ParentName,
                row.FkId is null
                    ? null
                    : violations.FirstOrDefault(v =>
                        string.Equals(v.TableName, row.TableName, StringComparison.Ordinal) && v.FkId == row.FkId)?.Rows ?? 0,
                perTable[row.TableName],
                tableRows.TryGetValue(row.TableName, out var total) ? total : 0));

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Counts the rows of each named table.
    /// </summary>
    /// <param name="dbContext">The database context.</param>
    /// <param name="tables">The table names, taken from <c>sqlite_master</c>.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <returns>Table name to row count.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the one place in this plugin where SQL is assembled rather than written, and
    /// it is not a shortcut.</b> SQLite has no catalogue view carrying row counts, and a table
    /// name cannot be a parameter - so a count per table needs its name inside the statement
    /// whichever way it is done. One <c>UNION ALL</c> is that same unavoidable interpolation,
    /// performed once instead of once per round trip.
    /// </para>
    /// <para>
    /// The names come from <c>sqlite_master</c>, so they are the database's own, never a
    /// caller's - this route takes no parameters at all. They are quoted anyway, by the two
    /// rules SQLite defines: an identifier in double quotes with embedded double quotes
    /// doubled, a literal in single quotes with embedded single quotes doubled. Relying on
    /// "the input is trusted" is how the next reader learns the wrong lesson from this method.
    /// </para>
    /// <para>
    /// <c>COUNT(*)</c> lets SQLite walk the smallest index rather than the table, so this costs
    /// far less than the foreign-key check it accompanies.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<string, long>> CountRowsAsync(
        JellyfinDbContext dbContext,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {
        var names = tables.ToList();
        if (names.Count == 0)
        {
            // Not defensive decoration: joining an empty list would produce an empty statement,
            // which is a syntax error rather than an empty result.
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var sql = string.Join(
            " UNION ALL ",
            names.Select(name =>
                $"SELECT {QuoteLiteral(name)} AS \"TableName\", COUNT(*) AS \"Rows\" FROM {QuoteIdentifier(name)}"));

        var rows = await dbContext.Database.SqlQueryRaw<TableRowCount>(sql)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(row => row.TableName, row => row.Rows, StringComparer.Ordinal);
    }

    /// <summary>
    /// Quotes a SQLite identifier.
    /// </summary>
    /// <param name="name">The identifier.</param>
    /// <returns>The identifier in double quotes, with embedded double quotes doubled.</returns>
    private static string QuoteIdentifier(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Quotes a SQLite string literal.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The value in single quotes, with embedded single quotes doubled.</returns>
    private static string QuoteLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Orders the findings deterministically, so two runs can be compared line by line.
    /// </summary>
    /// <param name="findings">The findings to order.</param>
    /// <returns>The findings by violation count, then table, then column.</returns>
    /// <remarks>
    /// Descending by <c>RowCount</c> first, because the shape of the damage is what a caller
    /// reads - the largest relation first, and the nulls last where they read as a footnote
    /// rather than as findings. Table and column break the tie, and together they are unique:
    /// one row per declared foreign key.
    /// </remarks>
    private static List<OrphanRowFindingDto> Sorted(IEnumerable<OrphanRowFindingDto> findings)
        => findings
            .OrderByDescending(finding => finding.RowCount ?? -1)
            .ThenBy(finding => finding.Table, StringComparer.Ordinal)
            .ThenBy(finding => finding.Column, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One row of <c>pragma_foreign_key_check</c>, already grouped.
    /// </summary>
    private sealed class ViolationRow
    {
        /// <summary>Gets or sets the child table.</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>Gets or sets which of the table's foreign keys was violated.</summary>
        public int FkId { get; set; }

        /// <summary>Gets or sets how many rows violate it.</summary>
        public int Rows { get; set; }
    }

    /// <summary>
    /// One table and how many rows it holds.
    /// </summary>
    private sealed class TableRowCount
    {
        /// <summary>Gets or sets the table.</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>Gets or sets how many rows it holds.</summary>
        public long Rows { get; set; }
    }

    /// <summary>
    /// One declared foreign key, or one table that declares none.
    /// </summary>
    private sealed class DeclaredRow
    {
        /// <summary>Gets or sets the table.</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>Gets or sets the foreign key's id within the table, null when there is none.</summary>
        public int? FkId { get; set; }

        /// <summary>Gets or sets the column holding the reference.</summary>
        public string? ColumnName { get; set; }

        /// <summary>Gets or sets the table it points at.</summary>
        public string? ParentName { get; set; }
    }
}
