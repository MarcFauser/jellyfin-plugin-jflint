using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Release folders that give every episode its own directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists: the caller moved 29.8 MB to compute a few hundred rows.</b> The "one
/// folder per episode" tab has two halves, and only one of them had a route. The other asked
/// for <c>/Items?Recursive=true&amp;IncludeItemTypes=Episode&amp;Fields=Path</c> and got
/// 31,655 items back - 33.2, 33.5 and 34.3 seconds over three runs after a warm-up - to
/// derive 216 release folders from pure path arithmetic. The computation is cheap; the
/// transfer is the cost.
/// </para>
/// <para>
/// <b>It is not the same question as <c>PerEpisodeFolder</c>, and the difference is which end
/// it reports.</b> That kind names a <c>Season</c> Jellyfin created whose <i>name</i> looks
/// like a file name - the child. This one names the <b>release folder holding at least three
/// of them</b> - the parent - and derives it from episode paths alone, so it holds whether or
/// not Jellyfin resolved anything. On the reference library that is the whole difference
/// between the two: the season half reports 0 and this one 215.
/// </para>
/// <para>
/// <b>The pair is a real control here, not two transports of one answer.</b> The database half
/// reads the raw <c>Path</c> column, the object-model half the materialised property, and those
/// are not the same string - Jellyfin stores the metadata and data directories as
/// <c>%MetadataPath%</c> and <c>%AppDataPath%</c>. That substitution is where this plugin's one
/// released path defect lived, and here it would not merely misprint a path: the grouping is
/// <i>on</i> the path, so a half that grouped stored spellings would produce different parents.
/// The database half therefore expands <b>before</b> grouping rather than before reporting.
/// </para>
/// <para>
/// <b>Both halves take the same base population by construction</b>, which is the failure the
/// caller warned about. Of 31,655 episode items only 26,884 carry a path; the rest are virtual.
/// If one half filtered on the path and the other did not, they would differ by thousands and
/// it would read as a defect where only the population differs. Both filter on a non-empty
/// path explicitly, so the agreement does not depend on <c>GetItemList</c> and a SQL
/// <c>WHERE</c> happening to exclude the same rows - and they do not: measured on v12,
/// <c>GetItemList</c> returned 26,151 episodes where HTTP reported 30,921.
/// </para>
/// <para>
/// Requires elevation, like everything else here, because the responses are paths.
/// </para>
/// </remarks>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface, used
/// by both halves - the object-model half for the episodes, and <b>both</b> for the configured
/// library locations that can never be a release.</param>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used to
/// expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class UnflattenedReleaseController(
    ILibraryManager libraryManager,
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets release folders laid out one directory per episode, via
    /// <see cref="ILibraryManager"/>.
    /// </summary>
    /// <param name="minFolders">How many per-episode folders make a release. Defaults to
    /// <see cref="UnflattenedReleaseRule.DefaultMinFolders"/>.</param>
    /// <response code="200">Findings returned.</response>
    /// <response code="400">The threshold is below what the criterion can mean.</response>
    /// <returns>One row per release folder.</returns>
    /// <remarks>
    /// The slow half of the pair, and slow for the same structural reason as every other
    /// object-model half: it materialises every episode in the library. A caller asks
    /// <c>UnflattenedReleaseDB</c> first and falls back to this one.
    /// </remarks>
    [HttpGet("UnflattenedRelease")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<UnflattenedReleaseDto>> GetUnflattenedReleases(
        [FromQuery] int minFolders = UnflattenedReleaseRule.DefaultMinFolders)
    {
        var refusal = Refuse(minFolders);
        if (refusal is not null)
        {
            return refusal;
        }

        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,

            // Asking for as little as possible, for cost rather than correctness: a default
            // DtoOptions carries every ItemField and EnableUserData, which adds four collection
            // Includes to one query. Path and SeriesName are columns on the item itself and
            // arrive either way.
            DtoOptions = new DtoOptions(false) { EnableUserData = false }
        })
            .Where(item => !string.IsNullOrEmpty(item.Path))
            .Select(item => (item.Path, (item as Episode)?.SeriesName));

        return Ok(UnflattenedReleaseRule.Evaluate(episodes, LibraryRoots(), minFolders));
    }

    /// <summary>
    /// Gets release folders laid out one directory per episode, straight from the database.
    /// </summary>
    /// <param name="minFolders">How many per-episode folders make a release. Defaults to
    /// <see cref="UnflattenedReleaseRule.DefaultMinFolders"/>.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Findings returned.</response>
    /// <response code="400">The threshold is below what the criterion can mean.</response>
    /// <returns>One row per release folder.</returns>
    /// <remarks>
    /// <para>
    /// The whole judgement runs in memory on purpose. SQLite has no <c>dirname</c>, so the
    /// parent of a path is <c>rtrim(Path, replace(replace(Path,'\','/'),'/',''))</c> - which
    /// works, has to be applied twice, and is unpleasant to read. It would also buy nothing:
    /// the expensive part is the transfer, and this route returns a few hundred rows either
    /// way. Grouping 26,884 short strings inside the server process costs nothing worth
    /// measuring.
    /// </para>
    /// <para>
    /// No <c>LIKE</c> pre-filter either, and that is the more important omission. It would
    /// have to be applied on this half only, and a filter one half applies and the other does
    /// not is how a pair stops being a control - which this plugin had already broken once, in
    /// the database half of <c>FileNameTitle</c>, and caught before shipping.
    /// </para>
    /// <para>
    /// <b><c>PrimaryVersionId == null</c> is the one filter this half DOES apply alone, and it
    /// exists to stop the halves disagreeing rather than to start it.</b> Jellyfin 12 merges
    /// alternate versions of an episode by itself, and its repository hides them from every
    /// object-model query - <c>ApplyGeneralFiltering</c> appends
    /// <c>PrimaryVersionId == null &amp;&amp; (OwnerId == null || ExtraType != null)</c> unless
    /// <c>IncludeOwnedItems</c> is set. The raw table has no such thing, so without this line a
    /// folder holding one episode stored as two stacked files arrives here as two rows, stops
    /// being a per-episode folder, and drops out of its release's count.
    /// </para>
    /// <para>
    /// Measured on the reference library at 12.28.0.0, both halves at
    /// <c>minFolders=3</c>: 215 folders on each side, the sets identical, and <b>one</b> folder
    /// carrying different numbers - <c>Royal.Pains.S01…</c>, 12 through
    /// <see cref="ILibraryManager"/> against 11 here, where one episode sits in the folder as
    /// <c>…teil-1-720p.mkv</c> and <c>…teil-2-720p.mkv</c>. Twelve is the right answer: the
    /// criterion counts episodes, this half was counting files. The comparison that found it had
    /// to be on the <i>counts</i>; the calling tool's acceptance run compared the sets over
    /// <c>Folder</c>, which is the right check for "do both halves find the same releases" and is
    /// blind to a disagreement inside a row.
    /// </para>
    /// <para>
    /// <b>Only the first half of Jellyfin's predicate is mirrored, deliberately.</b> The
    /// <c>OwnerId</c> branch measures zero on this library - had an owned non-extra episode with
    /// a path existed, this half would have reported a folder the other half does not, and the
    /// set comparison above would have shown it. Copying a condition that has never had an effect
    /// would be a guess dressed as symmetry; if one ever appears, the pair reports it.
    /// </para>
    /// <para>
    /// <b>On the 10.11 line the filter is a no-op, and the reason first written here was
    /// wrong.</b> It said "episode merging arrived with v12", which is true of <i>automatic</i>
    /// merging only: 10.11's <c>POST /Videos/MergeVersions</c> takes <c>.OfType&lt;Video&gt;()</c>
    /// and <c>Episode : Video</c>, so merging episodes by hand has always been possible. What
    /// really differs is the read side - measured across both shipped trees, <c>PrimaryVersionId
    /// == null</c> appears <b>0</b> times in 10.11 and <b>7</b> in v12, with the positive control
    /// that the 10.11 tree mentions the column in 31 files, so the search was not simply blind
    /// there.
    /// </para>
    /// <para>
    /// So on 10.11 <i>neither</i> half filters, and this line is a no-op exactly as long as
    /// nobody has merged episodes by hand. If someone has, this half reports <i>fewer</i>
    /// per-episode folders than its twin - the same disagreement that prompted the filter, with
    /// the signs swapped. Left unconditional rather than branched on the target framework: this
    /// library's owner never merges versions at all, by standing policy, and the case would
    /// surface as a handful of rows on the one route built to be read against its twin. A
    /// <c>#if</c> would buy exactness on a line where the case requires a deliberate act nobody
    /// here performs, at the price of the first version branch in this file.
    /// </para>
    /// </remarks>
    [HttpGet("UnflattenedReleaseDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<UnflattenedReleaseDto>>> GetUnflattenedReleasesFromDatabaseAsync(
        [FromQuery] int minFolders = UnflattenedReleaseRule.DefaultMinFolders,
        CancellationToken cancellationToken = default)
    {
        var refusal = Refuse(minFolders);
        if (refusal is not null)
        {
            return refusal;
        }

        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var rows = await dbContext.BaseItems
                .AsNoTracking()

                // PrimaryVersionId == null drops alternate versions, because the other half
                // drops them too - this is Jellyfin's own predicate, not a rule of ours. See
                // the remarks: without it this half counts FILES where the criterion counts
                // EPISODES, and the two halves put different numbers on the same folder.
                .Where(item => item.Type == episodeType
                               && item.Path != null
                               && item.PrimaryVersionId == null)
                .Select(item => new { item.Path, item.SeriesName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Expanded BEFORE the grouping, not before reporting. The grouping is ON the path,
            // so a stored spelling would produce a different parent rather than a differently
            // printed one - the pair would disagree about which folders exist.
            var episodes = rows.Select(row => (StoredPath.Expand(appHost, row.Path)!, row.SeriesName));

            return Ok(UnflattenedReleaseRule.Evaluate(episodes, LibraryRoots(), minFolders));
        }
    }

    /// <summary>
    /// Refuses a threshold that would change the question rather than tighten it.
    /// </summary>
    /// <param name="minFolders">The requested threshold.</param>
    /// <returns>The refusal, or null when the value is usable.</returns>
    private BadRequestObjectResult? Refuse(int minFolders)
        => minFolders < UnflattenedReleaseRule.MinimumThreshold
            ? BadRequest(string.Format(
                CultureInfo.InvariantCulture,
                "minFolders must be at least {0}; below that every folder holding a single episode would make its parent a release.",
                UnflattenedReleaseRule.MinimumThreshold))
            : null;

    /// <summary>
    /// Every configured library location.
    /// </summary>
    /// <returns>The locations, which can never be a release folder.</returns>
    /// <remarks>
    /// Read through <see cref="ILibraryManager"/> by <b>both</b> halves, so the bar is the same
    /// set on each. Handing the database half a list from the caller instead would have made it
    /// a parameter the two could disagree about.
    /// </remarks>
    private IEnumerable<string> LibraryRoots()
        => libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations);
}
