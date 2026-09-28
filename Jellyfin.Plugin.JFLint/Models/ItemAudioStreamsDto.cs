using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// The audio tracks of one movie or episode, including one that Jellyfin never probed.
/// </summary>
/// <remarks>
/// <c>MediaInfoDB</c> carries the same tracks but starts from the video stream, so an item
/// without any stream row is not in its answer at all. Measured on the reference library at
/// 12.39.0.0: three files Jellyfin never recorded a stream for, and all three turned out to be
/// zero-filled on disk. A "no track in language X" check built on that route cannot see them;
/// this one starts from the items instead.
/// </remarks>
/// <param name="Id">The item id.</param>
/// <param name="ItemType">The short type name, <c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Name">The item's name.</param>
/// <param name="SeriesName">The series name, null for a movie.</param>
/// <param name="Path">The item's path on disk.</param>
/// <param name="StreamCount">How many streams of any type Jellyfin recorded for the file -
/// video, audio, subtitle, attachment. <c>0</c> means it recorded none at all, which is how a
/// file ffprobe could not read looks, and is the only way to tell such a file from one that was
/// probed and simply has no audio: both have an empty <paramref name="AudioStreams"/>.</param>
/// <param name="AudioStreams">Every audio track of the file, ordered by stream index - the same
/// <see cref="AudioStreamDto"/> and the same promises as on <c>MediaInfoDB</c> and
/// <c>DuplicateMovie</c>. Never null; empty when the file has no audio track or was never
/// probed.</param>
public sealed record ItemAudioStreamsDto(
    Guid Id,
    string ItemType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SeriesName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    int StreamCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<AudioStreamDto> AudioStreams);
