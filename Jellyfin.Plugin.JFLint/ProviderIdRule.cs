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
