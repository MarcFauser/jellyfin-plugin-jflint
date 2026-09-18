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
/// <b>Only Imdb, Tvdb and Custom are inserted - Tmdb is not.</b> A series carrying nothing
/// but a Tmdb id therefore has no grouping id at all, which is worth stating because "has a
/// provider id" is the test one reaches for and it is the wrong one.
/// </para>
/// <para>
/// <b>The paragraph above describes Jellyfin 10.11, and v12 added a branch under it.</b>
/// There, <c>userdatakeys.Count &gt; 1</c> no longer decides <i>whether</i> to group, only
/// <i>which</i> key (<c>Series.cs:87</c>):
/// <code>
/// var groupingKey = userdatakeys.Count > 1 ? userdatakeys[0] : GetNameBasedGroupingKey();
/// private string GetNameBasedGroupingKey() => "series-" + Name.ToLowerInvariant();
/// </code>
/// So a series with none of the three groups on its lowercased name, and two same-named
/// unidentified folders merge silently. On 10.11 each fell back to its own id and stayed
/// separate - <c>GetNameBasedGroupingKey</c> does not exist on that branch.
/// <see cref="NameBasedReason"/> reports that case; <see cref="ImplausibleReason"/> cannot,
/// because it needs an id to judge and this class has none.
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
    /// The leading segment Jellyfin 12 writes when a series has no id to group on - see
    /// <c>Series.GetNameBasedGroupingKey</c>.
    /// </summary>
    public const string NameBasedKeyPrefix = "series-";

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
    /// <remarks>
    /// The formats themselves live in <see cref="ProviderIdRule"/> and are not restated here.
    /// This rule asks a narrower question - only the id that leads a presentation key can ever
    /// reach it, which <see cref="LeadingProvider"/> restricts to Custom, Tvdb and Imdb - but
    /// "what shape is a Tvdb id" has to have exactly one answer in this assembly. Custom needs
    /// no branch of its own: it is opaque by design and the shared rule is silent about it,
    /// which is the same verdict for the same reason.
    /// </remarks>
    public static string? ImplausibleReason(string? provider, string? value)
        => ProviderIdRule.ImplausibleReason(provider, value);

    /// <summary>
    /// Says why a shared key groups on the series name rather than on an id, or null when it
    /// does not.
    /// </summary>
    /// <param name="leadingProvider">The provider from <see cref="LeadingProvider"/>, or null
    /// when the row carries none of the three.</param>
    /// <param name="presentationKey">The key the group formed on.</param>
    /// <returns>The reason, or null when an id decided the group.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is a check on the key, which the rest of this class avoids - and the reason it
    /// is sound here is that it does not parse.</b> The objection to reading the key is that
    /// it cannot be split back apart; a prefix test splits nothing. It only asks whether
    /// <c>GetNameBasedGroupingKey</c> produced the leading segment.
    /// </para>
    /// <para>
    /// <b>Both conditions are needed, and the second is not redundant.</b> A custom id is
    /// opaque, so somebody may legitimately use <c>series-something</c> as one - that key
    /// starts with the prefix and was still chosen by an id. Requiring the absence of a
    /// leading provider separates the two. Conversely the prefix is what keeps this quiet on
    /// 10.11, where an id-less series falls back to its own guid and the prefix never appears.
    /// </para>
    /// <para>
    /// Only the caller knows the group has two or more members; a single series on a
    /// name-based key is not a finding, merely a series without ids.
    /// </para>
    /// </remarks>
    public static string? NameBasedReason(string? leadingProvider, string? presentationKey)
    {
        if (!string.IsNullOrWhiteSpace(leadingProvider))
        {
            return null;
        }

        if (string.IsNullOrEmpty(presentationKey)
            || !presentationKey.StartsWith(NameBasedKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return "grouped on the series name because no Imdb, Tvdb or Custom id is set";
    }
}
