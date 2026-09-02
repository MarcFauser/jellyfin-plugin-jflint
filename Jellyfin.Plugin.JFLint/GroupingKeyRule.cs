using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Decides whether a provider id is one Jellyfin should have been allowed to merge series on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a check on the key.</b> A series groups on
/// <c>userdatakeys[0]</c>, and <c>Series.GetUserDataKeys</c> inserts Imdb, then Tvdb, then
/// Custom at position 0 - so the leading id is Custom if present, else Tvdb, else Imdb.
/// <c>CreatePresentationUniqueKey</c> then appends the metadata language and every library
/// folder guid. The key is therefore a composite, and it cannot be split back apart: a custom
/// id may itself contain hyphens (<c>v-1984-final-battle</c>). The judgement is made on the
/// id read from the row's <c>ProviderIds</c> instead, which needs no parsing at all.
/// </para>
/// <para>
/// <b>Plausible means something different per provider</b>, which is the whole reason this
/// exists as a rule rather than as a "looks odd" filter. TVDB ids are positive integers, so
/// <c>-1</c> and <c>0</c> are impossible; IMDb ids carry a <c>tt</c> prefix; and a custom id
/// is <b>opaque by design</b> and may be anything at all. A blanket filter would report every
/// legitimate custom merge group, which is the kind of false alarm that gets a check ignored.
/// </para>
/// <para>
/// The occasion: two <c>V</c> folders were both given <c>&lt;tvdbid&gt;-1&lt;/tvdbid&gt;</c> as a
/// guard against being merged, and were merged with each other - a sentinel is not a unique
/// id, it is a match like any other. Jellyfin logs such a merge with no line at all.
/// </para>
/// </remarks>
internal static class GroupingKeyRule
{
    /// <summary>
    /// Names the provider whose id leads the grouping key, in Jellyfin's own precedence.
    /// </summary>
    /// <param name="providerIds">The row's provider ids, case-insensitive by key.</param>
    /// <param name="value">The id that leads the key.</param>
    /// <returns>The provider name, or null when the row carries none of the three.</returns>
    public static string? LeadingProvider(IReadOnlyDictionary<string, string>? providerIds, out string? value)
    {
        value = null;
        if (providerIds is null)
        {
            return null;
        }

        // The order is Jellyfin's, not a preference: Custom is inserted at 0 last and
        // therefore wins, then Tvdb, then Imdb.
        foreach (var provider in new[] { MetadataProvider.Custom, MetadataProvider.Tvdb, MetadataProvider.Imdb })
        {
            var name = provider.ToString();
            if (providerIds.TryGetValue(name, out var found) && !string.IsNullOrWhiteSpace(found))
            {
                value = found;
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Says why an id could not be a real one, or null when it could.
    /// </summary>
    /// <param name="provider">The provider name from <see cref="LeadingProvider"/>.</param>
    /// <param name="value">The id value.</param>
    /// <returns>The reason, or null when the id is plausible or cannot be judged.</returns>
    public static string? ImplausibleReason(string? provider, string? value)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(value))
        {
            // No id of the three led the key, so the grouping came from somewhere this rule
            // knows nothing about. Silence, not a finding - a rule that judges what it cannot
            // see produces exactly the false alarms it was written to avoid.
            return null;
        }

        if (string.Equals(provider, nameof(MetadataProvider.Custom), StringComparison.Ordinal))
        {
            // Opaque on purpose. Any value is a legitimate merge key.
            return null;
        }

        if (string.Equals(provider, nameof(MetadataProvider.Tvdb), StringComparison.Ordinal))
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                return $"Tvdb id '{value}' is not a positive integer";
            }

            return null;
        }

        if (string.Equals(provider, nameof(MetadataProvider.Imdb), StringComparison.Ordinal))
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
