using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JFLint.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Video properties per item, including the colour range the stock API cannot return cheaply.
/// </summary>
/// <remarks>
/// Requires elevation because the responses contain media file paths.
/// </remarks>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used
/// to expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class MediaInfoController(
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory) : ControllerBase
{
    /// <summary>
    /// Gets the video properties of every movie and episode that has a video stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>BaseItemDto</c> has no <c>VideoRange</c> - checked against
    /// the running OpenAPI, 153 properties and neither of the two - so the stock way to read
    /// it is <c>Fields=MediaStreams</c>, which ships every stream of every item. Measured on
    /// the reference library: <c>Fields=Path,Width,Height</c> costs 15 s and 29 MB for the
    /// episodes, <c>Fields=Path,MediaStreams</c> costs <b>76 s and 117 MB</b> for the same
    /// rows.
    /// </para>
    /// <para>
    /// <b>The classification is Jellyfin's, not ours.</b> Dolby Vision profiles 5/7/8/10, the
    /// RPU and base-layer flags, the compatibility id, HDR10+, the <c>dovi</c>/<c>dvh1</c>/
    /// <c>dvhe</c>/<c>dav1</c> codec tags and the <c>smpte2084</c>/<c>arib-std-b67</c> colour
    /// transfers are all read by <c>MediaStream.GetVideoColorRange()</c>, which is public. The
    /// columns are filled into a <see cref="MediaStream"/> and that method is called; nothing
    /// here reimplements the rule, so it cannot drift from the server's own answer.
    /// </para>
    /// <para>
    /// <b>This route has no twin, and that is deliberate.</b> Every other query here exists
    /// twice so each half is the other's cross-check. That is impossible for this one and
    /// would be worth little if it were. Impossible, because there is no bulk read:
    /// <c>MediaStreamQuery.ItemId</c> is a non-nullable <c>Guid</c> and
    /// <c>MediaStreamRepository.TranslateQuery</c> filters on it unconditionally, so
    /// <c>IMediaSourceManager.GetMediaStreams</c> can only be asked one item at a time - tens
    /// of thousands of calls, each opening its own context, which would be slower than the
    /// 76 s fallback it is meant to back up. Worth little, because both halves would end in
    /// the same <c>GetVideoColorRange()</c>: a second transport of one derivation is not a
    /// second opinion about it. A caller falls back to <c>Fields=MediaStreams</c> instead.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Video properties returned.</response>
    /// <returns>One row per movie or episode that has a video stream.</returns>
    [HttpGet("MediaInfoDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MediaInfoDto>>> GetMediaInfoFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        var episodeType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];
        var shortNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [movieType] = nameof(BaseItemKind.Movie),
            [episodeType] = nameof(BaseItemKind.Episode)
        };

        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // One join, not one query per item. Width and Height come from the ITEM rather
            // than the stream, so they are the same numbers every other route here reports;
            // the stream carries its own pair and they are not always equal.
            var rows = await dbContext.MediaStreamInfos
                .AsNoTracking()
                .Where(stream => stream.StreamType == MediaStreamTypeEntity.Video)
                .Join(
                    dbContext.BaseItems.Where(item =>
                        (item.Type == movieType || item.Type == episodeType) && !item.IsVirtualItem),
                    stream => stream.ItemId,
                    item => item.Id,
                    (stream, item) => new
                    {
                        item.Id,
                        item.Type,
                        item.Name,
                        item.SeriesName,
                        item.Path,
                        item.Width,
                        item.Height,
                        stream.StreamIndex,
                        stream.CodecTag,
                        stream.DvProfile,
                        stream.RpuPresentFlag,
                        stream.BlPresentFlag,
                        stream.DvBlSignalCompatibilityId,
                        stream.Hdr10PlusPresentFlag,
                        stream.ColorTransfer
                    })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // An item may hold more than one video stream; the first by index is the one the
            // server treats as the video, and reporting all of them would put an item into the
            // answer twice under one id.
            var findings = rows
                .GroupBy(row => row.Id)
                .Select(group => group.OrderBy(row => row.StreamIndex).First())
                .Select(row =>
                {
                    var stream = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        CodecTag = row.CodecTag,
                        DvProfile = row.DvProfile,
                        RpuPresentFlag = row.RpuPresentFlag,
                        BlPresentFlag = row.BlPresentFlag,
                        DvBlSignalCompatibilityId = row.DvBlSignalCompatibilityId,
                        Hdr10PlusPresentFlag = row.Hdr10PlusPresentFlag,
                        ColorTransfer = row.ColorTransfer
                    };

                    var (videoRange, videoRangeType) = stream.GetVideoColorRange();

                    return new MediaInfoDto(
                        row.Id,
                        shortNames[row.Type],
                        row.Name,
                        row.SeriesName,
                        StoredPath.Expand(appHost, row.Path),
                        row.Width,
                        row.Height,
                        videoRange,
                        videoRangeType);
                });

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Orders findings deterministically, so two runs can be compared line by line.
    /// </summary>
    /// <param name="items">The items to order.</param>
    /// <returns>The items by series, name, path and id.</returns>
    private static List<MediaInfoDto> Sorted(IEnumerable<MediaInfoDto> items)
        => items
            .OrderBy(item => item.SeriesName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();
}
