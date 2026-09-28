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
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.JFLint.Controllers;

/// <summary>
/// Video properties and audio tracks per item, including the colour range the stock API cannot
/// return cheaply.
/// </summary>
/// <remarks>
/// Requires elevation because the responses contain media file paths.
/// </remarks>
/// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface, used
/// to expand the stored form of a path - see <see cref="StoredPath"/>.</param>
/// <param name="dbContextFactory">Factory for the Jellyfin database context.</param>
/// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface, used
/// to read an audio track's language the way Jellyfin does - see <see cref="StreamLanguage"/>.</param>
[ApiController]
[Route("JFLint")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class MediaInfoController(
    IItemTypeLookup itemTypeLookup,
    IServerApplicationHost appHost,
    IDbContextFactory<JellyfinDbContext> dbContextFactory,
    ILocalizationManager localization) : ControllerBase
{
    /// <summary>
    /// The tracks of a <c>MediaInfoDB</c> call that neither sends them nor filters by them - an
    /// empty lookup, so the query is not run at all.
    /// </summary>
    private static readonly ILookup<Guid, AudioStreamDto> NoTracks =
        Array.Empty<AudioStreamDto>().ToLookup(_ => Guid.Empty);

    /// <summary>
    /// Gets the video properties and audio tracks of every movie and episode that has a video
    /// stream.
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
    /// <c>dvhe</c>/<c>dav1</c> codec tags, the <c>smpte2084</c>/<c>arib-std-b67</c> colour
    /// transfers and - since Jellyfin 12 - the <c>bt2020nc</c> colour space and <c>bt2020</c>
    /// primaries are all read by <c>MediaStream.GetVideoColorRange()</c>, which is public. The
    /// columns are filled into a <see cref="MediaStream"/> and that method is called; nothing
    /// here reimplements the rule, so it cannot drift from the server's own answer.
    /// </para>
    /// <para>
    /// <b>"Cannot drift" holds only for the rule, not for its inputs</b>, and that distinction
    /// cost 1118 rows. Calling the server's own method guarantees the same verdict for the same
    /// stream; it guarantees nothing about whether every field that verdict consults has been
    /// filled. Jellyfin 12 began consulting two more, this route kept filling the old eight,
    /// and the result was a confident wrong answer that no cross-check here could catch -
    /// because the only cross-check for this route is the server, and nobody was asking it.
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
    /// <para>
    /// <b>That fallback stopped being a clean cross-check on Jellyfin 12, and the sentence above
    /// is left standing rather than edited away.</b> This query reads
    /// <c>dbContext.BaseItems</c> raw and emits one row per item id, alternate versions
    /// included; <c>Fields=MediaStreams</c> goes through <c>BaseItemRepository.TranslateQuery</c>,
    /// which appends <c>PrimaryVersionId == null &amp;&amp; (OwnerId == null || ExtraType != null)</c>
    /// and folds those files into the primary's <c>MediaSources</c>. So on the v12 line the two
    /// disagree <b>by construction</b>, and the difference is not a defect in either.
    /// </para>
    /// <para>
    /// This is not an argument for filtering here. One row per file is the right shape for a
    /// route about video properties - a stacked file has its own codec, resolution and colour
    /// metadata, and hiding it is how the 1118-row defect above stayed invisible. Measured on
    /// the reference library: 23 episode rows carry an alternate-version link, so a residue of
    /// that order is expected rather than alarming. What it costs is the oracle: this route's
    /// only cross-check now needs its own correction applied before the numbers can be compared,
    /// and a standing unexplained residue is exactly what would hide the next real defect.
    /// </para>
    /// <para>
    /// <b>The audio tracks come only on request</b> (<c>includeAudio=true</c>), since 12.41.0.0.
    /// 12.39.0.0 added them unconditionally, and measured warm that took the route from a median
    /// of 1,006 ms and 13.2 MB to 1,939 ms and 18.9 MB - paid on every call, by callers that only
    /// want the video properties. A caller that wants the tracks for a language check is better
    /// served by <c>AudioStreamsDB</c>, which also lists the files this route cannot: it starts
    /// from the video stream, so a file Jellyfin recorded no stream for has no row here at all.
    /// </para>
    /// <para>
    /// <b><c>withoutAudioLanguage</c> is <c>AudioStreamsDB</c>'s <c>withoutLanguage</c> on this
    /// route</b>, since 12.42.0.0: the same rule, the same filter in SQL first, the same 400 for a
    /// language the server does not know. It narrows the rows and does not add the tracks - that
    /// is still <c>includeAudio=true</c>. A file without a video stream is not in this route
    /// either way, the never-probed ones included; <c>AudioStreamsDB</c> lists those.
    /// </para>
    /// </remarks>
    /// <param name="includeAudio">Optional. <c>true</c> adds every file's audio tracks as
    /// <c>AudioStreams</c>; without it the field is absent.</param>
    /// <param name="withoutAudioLanguage">Optional. Only files without an audio track in this
    /// language - <c>de</c>, <c>deu</c>, <c>ger</c> or <c>German</c> all mean German. Absent or
    /// empty means every file.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Video properties returned.</response>
    /// <response code="400"><c>withoutAudioLanguage</c> names no language the server knows.</response>
    /// <returns>One row per movie or episode that has a video stream, or per such file without a
    /// track in the given language.</returns>
    [HttpGet("MediaInfoDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<MediaInfoDto>>> GetMediaInfoFromDatabaseAsync(
        [FromQuery] bool includeAudio,
        [FromQuery] string? withoutAudioLanguage,
        CancellationToken cancellationToken)
    {
        var excluded = ResolveLanguage(withoutAudioLanguage, out var refused);
        if (refused)
        {
            return BadRequest(
                "withoutAudioLanguage must name a language the server knows - a code such as de, deu or ger, or a name such as German.");
        }

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
            var candidates = MoviesAndEpisodes(dbContext, movieType, episodeType);
            if (excluded is not null)
            {
                candidates = WithoutLanguage(dbContext, candidates, excluded);
            }

            // One join, not one query per item. Width and Height come from the ITEM rather
            // than the stream, so they are the same numbers every other route here reports;
            // the stream carries its own pair and they are not always equal.
            var rows = await dbContext.MediaStreamInfos
                .AsNoTracking()
                .Where(stream => stream.StreamType == MediaStreamTypeEntity.Video)
                .Join(
                    candidates,
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

                        // The ffprobe codec_name, lower case (h264, hevc, mpeg4) - reported as it
                        // is stored. Not CodecTag: that is the container's fourcc (avc1, hvc1,
                        // dvh1) and serves the colour range below.
                        stream.Codec,
                        stream.CodecTag,
                        stream.DvProfile,
                        stream.RpuPresentFlag,
                        stream.BlPresentFlag,
                        stream.DvBlSignalCompatibilityId,
                        stream.Hdr10PlusPresentFlag,
                        stream.ColorTransfer,

                        // Jellyfin 12 added a validation step to GetVideoColorRange: once a
                        // Dolby Vision profile has been derived it must also see bt2020nc and
                        // bt2020 here, or the result is downgraded to DOVIInvalid. Leaving them
                        // unset cost 1118 misclassified rows on the reference library, against
                        // a server that answered DOVIWithHDR10 for the same items. Measured on
                        // 10.11.11 as well, where they change nothing - so this is not a v12
                        // branch, it is two columns that should always have been read.
                        stream.ColorSpace,
                        stream.ColorPrimaries
                    })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Read when they are sent or when the language filter needs them to decide what SQL
            // could not, and then as a second query instead of a second join into the one above:
            // that one keeps a single video stream per item, and joining audio into it would
            // multiply its rows by the track count before the grouping throws them away.
            var tracksByItem = includeAudio || excluded is not null
                ? await ReadAudioTracksAsync(dbContext, candidates, cancellationToken).ConfigureAwait(false)
                : NoTracks;

            // An item may hold more than one video stream; the first by index is the one the
            // server treats as the video, and reporting all of them would put an item into the
            // answer twice under one id.
            var findings = rows
                .GroupBy(row => row.Id)
                .Select(group => group.OrderBy(row => row.StreamIndex).First())
                .Where(row => excluded is null
                    || !tracksByItem[row.Id].Any(track => StreamLanguage.IsIn(track.Language, excluded)))
                .Select(row =>
                {
                    // Codec is filled although GetVideoColorRange does not read it today - checked
                    // on release-10.11.z and v12.1, where it reads CodecTag and not Codec. It is
                    // read here anyway, and an input left empty because the method did not need
                    // it yet is exactly how the 1118-row defect described above came about.
                    var stream = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = row.Codec,
                        CodecTag = row.CodecTag,
                        DvProfile = row.DvProfile,
                        RpuPresentFlag = row.RpuPresentFlag,
                        BlPresentFlag = row.BlPresentFlag,
                        DvBlSignalCompatibilityId = row.DvBlSignalCompatibilityId,
                        Hdr10PlusPresentFlag = row.Hdr10PlusPresentFlag,
                        ColorTransfer = row.ColorTransfer,
                        ColorSpace = row.ColorSpace,
                        ColorPrimaries = row.ColorPrimaries
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
                        videoRangeType,
                        row.Codec,
                        includeAudio ? tracksByItem[row.Id].ToList() : null);
                });

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Gets the audio tracks of every movie and episode, including one Jellyfin never probed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists next to <c>MediaInfoDB</c>, which carries the same tracks.</b> That
    /// route starts from the video stream, so an item Jellyfin recorded no stream for is not in
    /// its answer at all - and a "no track in language X" check built on it cannot flag the one
    /// file that has no track in any language. Measured at 12.39.0.0's acceptance: three such
    /// files on the reference library, all three zero-filled on disk. This route starts from the
    /// items and left-joins the tracks, so every movie and episode gets a row.
    /// </para>
    /// <para>
    /// The second reason is cost: the tracks roughly doubled <c>MediaInfoDB</c> (median
    /// 1,006 ms to 1,939 ms, 13.2 MB to 18.9 MB), and the calling tool reads that route twice per
    /// sweep. Since this route exists, <c>MediaInfoDB</c> sends them only on
    /// <c>includeAudio=true</c>.
    /// </para>
    /// <para>
    /// <b>One row per file, and no twin</b> - both for the same reasons as <c>MediaInfoDB</c>.
    /// The items are read raw from <c>BaseItems</c>, so an alternate version is a row of its own,
    /// where <c>Fields=MediaStreams</c> folds it into its primary on Jellyfin 12. And
    /// <c>IMediaSourceManager.GetMediaStreams</c> answers one item at a time, so a library half
    /// would be tens of thousands of calls ending in the same stored rows.
    /// </para>
    /// <para>
    /// <b><c>withoutLanguage</c> answers the question the caller asks, on the server</b>, so the
    /// rows for "has a German track" never leave it. A row is dropped only when one of its tracks
    /// names that language (<see cref="StreamLanguage.IsIn"/>); everything else stays - a file
    /// whose tracks name other languages, a file with a track that names none, and a file with no
    /// track at all. The last two are not "no German track" in the same sense as the first, and
    /// the rows keep their tracks and <c>StreamCount</c> so the caller can tell them apart.
    /// </para>
    /// <para>
    /// Since 12.41.1.0 the filter runs in the database first, so the files that have the language
    /// are not read at all; 12.41.0.0 read every track and filtered in memory, which made the
    /// filtered answer barely cheaper than the full one. The database drops only what it can
    /// decide exactly, and <see cref="StreamLanguage.IsIn"/> still decides the rest.
    /// </para>
    /// </remarks>
    /// <param name="withoutLanguage">Optional. Only files without an audio track in this
    /// language - <c>de</c>, <c>deu</c>, <c>ger</c> or <c>German</c> all mean German. Absent or
    /// empty means every file.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <response code="200">Audio tracks returned.</response>
    /// <response code="400"><c>withoutLanguage</c> names no language the server knows. Refused
    /// rather than answered with an empty list, which would read as "every file has a track in
    /// it".</response>
    /// <returns>One row per movie or episode that is not virtual, or per such file without a track
    /// in the given language.</returns>
    [HttpGet("AudioStreamsDB")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ItemAudioStreamsDto>>> GetAudioStreamsFromDatabaseAsync(
        [FromQuery] string? withoutLanguage,
        CancellationToken cancellationToken)
    {
        var excluded = ResolveLanguage(withoutLanguage, out var refused);
        if (refused)
        {
            return BadRequest(
                "withoutLanguage must name a language the server knows - a code such as de, deu or ger, or a name such as German.");
        }

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
            var candidates = MoviesAndEpisodes(dbContext, movieType, episodeType);
            if (excluded is not null)
            {
                candidates = WithoutLanguage(dbContext, candidates, excluded);
            }

            var items = await candidates
                .AsNoTracking()
                .Select(item => new { item.Id, item.Type, item.Name, item.SeriesName, item.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Grouped, not a count per item. An item absent from this dictionary has no stream
            // row at all - the grouping runs over every stream of these items - so the default
            // below is the real count, not a stand-in for a value that went missing.
            var streamCounts = await dbContext.MediaStreamInfos
                .AsNoTracking()
                .Join(
                    candidates,
                    stream => stream.ItemId,
                    item => item.Id,
                    (stream, item) => stream.ItemId)
                .GroupBy(itemId => itemId)
                .Select(group => new { ItemId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.ItemId, row => row.Count, cancellationToken)
                .ConfigureAwait(false);

            var tracksByItem = await ReadAudioTracksAsync(dbContext, candidates, cancellationToken)
                .ConfigureAwait(false);

            var findings = items
                .Where(item => excluded is null
                    || !tracksByItem[item.Id].Any(track => StreamLanguage.IsIn(track.Language, excluded)))
                .Select(item => new ItemAudioStreamsDto(
                    item.Id,
                    shortNames[item.Type],
                    item.Name,
                    item.SeriesName,
                    StoredPath.Expand(appHost, item.Path),
                    streamCounts.GetValueOrDefault(item.Id),
                    tracksByItem[item.Id].ToList()));

            return Ok(Sorted(findings));
        }
    }

    /// <summary>
    /// Resolves an optional language parameter.
    /// </summary>
    /// <param name="value">What the caller sent.</param>
    /// <param name="refused">True when a value was sent that names no language the server
    /// knows - the caller answers 400, not an empty list, which would read as "every file has a
    /// track in it".</param>
    /// <returns>The language's codes, or null for no filter - absent, empty or blank.</returns>
    private IReadOnlySet<string>? ResolveLanguage(string? value, out bool refused)
    {
        refused = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var codes = StreamLanguage.CodesOf(localization, value);
        refused = codes is null;
        return codes;
    }

    /// <summary>
    /// Narrows the items to those without an audio track in the language, in SQL - the part of
    /// the filter the database can decide exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// So the items that have the language - 27,000 of 29,000 for German on the reference
    /// library - are never read, and neither are their tracks. Measured on
    /// <c>AudioStreamsDB?withoutLanguage=de</c>: 1,659 ms filtering in memory, 684 ms with this.
    /// </para>
    /// <para>
    /// SQL drops only an item with a track whose STORED code is one of the language's codes
    /// exactly, as listed or upper case. Such a track matches <see cref="StreamLanguage.IsIn"/>
    /// as well, since the only thing Jellyfin changes on the way out is a B code into its T code,
    /// and both are in the set. Everything SQL cannot decide exactly - <c>de-DE</c>, a padded or
    /// mixed-case code - is kept, and every caller must still apply <c>IsIn</c> to what comes
    /// back, which remains the rule.
    /// </para>
    /// <para>
    /// Two primitives only: an array <c>Contains</c>, which <c>FileNameTitleDB</c> has run on a
    /// 10.11 server since 2026-08, and a subquery <c>Contains</c> (<c>NOT IN (SELECT ...)</c>) - an
    /// old EF translation, but one that has not run on EF Core 9 here, because the reference
    /// server has been on 12 since 2026-09-10.
    /// </para>
    /// </remarks>
    /// <param name="dbContext">The open database context.</param>
    /// <param name="items">The items to narrow.</param>
    /// <param name="codes">What <see cref="StreamLanguage.CodesOf"/> returned.</param>
    /// <returns>The query, not yet run.</returns>
    private static IQueryable<BaseItemEntity> WithoutLanguage(
        JellyfinDbContext dbContext, IQueryable<BaseItemEntity> items, IReadOnlySet<string> codes)
    {
        var exact = codes
            .Concat(codes.Select(code => code.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var speaking = dbContext.MediaStreamInfos
            .Where(stream => stream.StreamType == MediaStreamTypeEntity.Audio
                && stream.Language != null
                && exact.Contains(stream.Language))
            .Select(stream => stream.ItemId);
        return items.Where(item => !speaking.Contains(item.Id));
    }

    /// <summary>
    /// The items both routes here report on: every movie and episode that is not virtual, read
    /// raw from <c>BaseItems</c> - so an alternate version is an item of its own.
    /// </summary>
    /// <param name="dbContext">The open database context.</param>
    /// <param name="movieType">The stored type name of a movie.</param>
    /// <param name="episodeType">The stored type name of an episode.</param>
    /// <returns>The query, not yet run.</returns>
    private static IQueryable<BaseItemEntity> MoviesAndEpisodes(
        JellyfinDbContext dbContext, string movieType, string episodeType)
        => dbContext.BaseItems.Where(item =>
            (item.Type == movieType || item.Type == episodeType) && !item.IsVirtualItem);

    /// <summary>
    /// Reads every audio track of the given items in one query rather than one per item.
    /// </summary>
    /// <param name="dbContext">The open database context.</param>
    /// <param name="items">The items whose tracks are wanted.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the framework.</param>
    /// <returns>The tracks per item id, each group ordered by stream index. A lookup answers an
    /// empty sequence for an item without tracks, so every caller gets a list - empty meaning
    /// "no audio track", never null.</returns>
    private async Task<ILookup<Guid, AudioStreamDto>> ReadAudioTracksAsync(
        JellyfinDbContext dbContext, IQueryable<BaseItemEntity> items, CancellationToken cancellationToken)
    {
        var tracks = await dbContext.MediaStreamInfos
            .AsNoTracking()
            .Where(stream => stream.StreamType == MediaStreamTypeEntity.Audio)
            .Join(
                items,
                stream => stream.ItemId,
                item => item.Id,
                (stream, item) => new
                {
                    stream.ItemId,
                    stream.StreamIndex,
                    stream.Codec,
                    stream.Profile,
                    stream.Language,
                    stream.ChannelLayout,
                    stream.Channels
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A lookup keeps the source order inside each group, so ordering once up front orders
        // every item's tracks.
        return tracks
            .OrderBy(track => track.StreamIndex)
            .ToLookup(
                track => track.ItemId,
                track => new AudioStreamDto(
                    track.StreamIndex,
                    track.Codec,
                    track.Profile,
                    StreamLanguage.AsJellyfinReadsIt(localization, track.Language),
                    track.ChannelLayout,
                    track.Channels));
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

    /// <summary>
    /// Orders findings deterministically, so two runs can be compared line by line.
    /// </summary>
    /// <param name="items">The items to order.</param>
    /// <returns>The items by series, name, path and id.</returns>
    private static List<ItemAudioStreamsDto> Sorted(IEnumerable<ItemAudioStreamsDto> items)
        => items
            .OrderBy(item => item.SeriesName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();
}
