using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Decides whether a provider id could identify anything, per provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>A single numeric predicate is useless here, and that is measured rather than argued.</b>
/// Asking "is every provider id a positive integer" of the reference library reports <b>678
/// values across 512 of its 527 series</b> - because <c>TvdbSlug</c> is text by design (515 of
/// them), <c>TvdbCollection</c> names a group rather than a title, and <c>Custom</c> is opaque
/// on purpose. The same question asked per provider reports 192 values across 13 series, which
/// is the actual fault. A check that flags everything is worth less than no check, because the
/// first thing anyone does with it is stop reading it.
/// </para>
/// <para>
/// <b>Silence is the default for anything not listed.</b> An unknown provider may use any shape
/// at all, and a rule that judges what it cannot see produces exactly the false alarms it was
/// written to avoid. The listed set is therefore a positive list: adding a provider is a
/// deliberate act, forgetting one costs a missed finding rather than a wrong one.
/// </para>
/// <para>
/// <see cref="GroupingKeyRule"/> delegates here rather than restating the formats. It asks a
/// narrower question - only the id that leads a presentation key can matter there - but "what
/// shape is a Tvdb id" has to have one answer in this assembly. Two rules that look alike are
/// two rules that drift apart.
/// </para>
/// </remarks>
internal static class ProviderIdRule
{
    /// <summary>
    /// The providers whose id must be a positive integer.
    /// </summary>
    /// <remarks>
    /// <c>TvdbSlug</c> and <c>TmdbCollection</c> are deliberately absent: the first is a text
    /// slug, the second identifies a collection the title belongs to rather than the title.
    /// Judging either as a number would report the whole library.
    /// </remarks>
    private static readonly HashSet<string> NumericProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tvdb", "Tmdb", "AniDb", "AniList", "AniSearch", "TvRage"
    };

    /// <summary>
    /// Says why a value will hurt a consumer that reads it as a number, or null when it will not.
    /// </summary>
    /// <param name="provider">The provider name, for the message only.</param>
    /// <param name="value">The id value.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is a different question from <see cref="ImplausibleReason"/>, and the difference
    /// was paid for.</b> That one asks whether an id could identify a title, which is what
    /// matters when two series merge onto it. This one asks whether a value will <i>break
    /// something downstream</i>, and the answer is not the same: <c>abc</c> identifies nothing
    /// and harms nobody, while <c>-1</c> does both.
    /// </para>
    /// <para>
    /// <b>Provider ids are strings and the rule treats them as such.</b> The harmful class is
    /// not "not a number" but "parses as a number and is not a usable one" - measured against
    /// the consumer that prompted this: SkipMe.db reads an id with
    /// <c>int.TryParse(..., NumberStyles.Integer, ...)</c> and <b>no sign check</b>, then omits
    /// a null field entirely (<c>JsonIgnoreCondition.WhenWritingNull</c>). So <c>-1</c> and
    /// <c>0</c> are sent and refused with 400, while <c>none</c> fails the parse, becomes null,
    /// and is never sent. The same shape holds for any consumer that parses before sending.
    /// </para>
    /// <para>
    /// That is why no list of accepted sentinel words exists here, and deliberately so. A list
    /// would have to be guessed and would flag the next word somebody chooses; the parse does
    /// not care whether the value is <c>none</c>, <c>skip</c> or <c>-</c>. It is also why
    /// "set it to 0" looks like a fix and is not one.
    /// </para>
    /// <para>
    /// Provider-independent on purpose. The harm comes from the value, not from which key it
    /// sits under - a consumer reading an unknown provider numerically breaks just the same.
    /// </para>
    /// </remarks>
    public static string? NonPositiveNumberReason(string? provider, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number > 0)
        {
            // Either not a number at all - which is the safe state, because a consumer that
            // parses will drop it - or a usable positive id.
            return null;
        }

        var name = string.IsNullOrWhiteSpace(provider) ? "provider" : provider;
        return $"{name} id '{value}' reads as the number {number.ToString(CultureInfo.InvariantCulture)}, which no provider issues - a consumer that parses it sends it and is refused";
    }

    /// <summary>
    /// Says why an id could not identify anything, or null when it could or cannot be judged.
    /// </summary>
    /// <param name="provider">The provider name, as it appears in <c>ProviderIds</c>.</param>
    /// <param name="value">The id value.</param>
    /// <returns>The reason, or null.</returns>
    public static string? ImplausibleReason(string? provider, string? value)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(value))
        {
            // Nothing to judge. An absent id is not a wrong id, and Jellyfin reads an empty
            // value as "has this provider at all" rather than as a value - see
            // ProviderIdsExtensions.TryGetProviderId, which reports an empty id as not found.
            return null;
        }

        if (NumericProviders.Contains(provider))
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                return $"{provider} id '{value}' is not a positive integer";
            }

            return null;
        }

        if (string.Equals(provider, nameof(MetadataProvider.Imdb), StringComparison.OrdinalIgnoreCase))
        {
            // tt plus digits. Nothing else is an IMDb title id.
            if (value.Length < 3
                || !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                || !AllDigits(value, 2))
            {
                return $"Imdb id '{value}' is not of the form tt<digits>";
            }

            return null;
        }

        // Custom, TvdbSlug, TmdbCollection and everything unknown: opaque by design or unknown
        // to this rule, and in both cases it has nothing to say.
        return null;
    }

    /// <summary>
    /// True when every character from <paramref name="start"/> on is an ASCII digit.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <param name="start">The index to start at.</param>
    /// <returns>Whether the tail is all digits.</returns>
    private static bool AllDigits(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
