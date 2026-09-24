using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JFLint.Models;

/// <summary>
/// One audio track of a file, as Jellyfin stores it.
/// </summary>
/// <remarks>
/// Raw values under Jellyfin's own <c>MediaStream</c> names, so a caller can compare them 1:1
/// with <c>Fields=MediaStreams</c>; turning them into display text is the caller's business.
/// Every field is serialised, null included - the server's serialiser drops nulls, and a
/// caller reading this from PowerShell under StrictMode would otherwise fail on a missing
/// <c>Profile</c> where it means "no profile".
/// </remarks>
/// <param name="Index">The stream index within the file.</param>
/// <param name="Codec">ffprobe's <c>codec_name</c>, lower case: <c>dts</c>, <c>truehd</c>,
/// <c>eac3</c>.</param>
/// <param name="Profile">The codec profile, e.g. <c>DTS-HD MA + DTS:X IMAX</c>, null when
/// ffprobe reported none.</param>
/// <param name="Language">The language tag as stored, e.g. <c>deu</c>, null when untagged.</param>
/// <param name="ChannelLayout">The channel layout, e.g. <c>5.1</c> or <c>stereo</c>.</param>
/// <param name="Channels">The channel count.</param>
public sealed record AudioStreamDto(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Codec,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Profile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Language,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ChannelLayout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Channels);
