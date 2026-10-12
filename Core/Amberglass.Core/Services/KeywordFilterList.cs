using Amberglass.Core.Interfaces;

namespace Amberglass.Core.Services;

/// <summary>
/// How the filter panel's keyword checklist is arranged.
///
/// Its own class because the order in which the two steps happen matters and is easy to get wrong
/// the next time someone reads the one-line version.
/// </summary>
public static class KeywordFilterList
{
    /// <summary>
    /// The keywords to show: the most-used ones up to <paramref name="cap"/>, arranged
    /// alphabetically for reading.
    ///
    /// <para>Alphabetical rather than by count, because someone at this list is looking for a
    /// particular word and its popularity says nothing about where to find it. The counts are still
    /// shown beside each one; they simply no longer decide the order.</para>
    ///
    /// <para>The cap is applied <b>first</b>, while the input is still in most-used order, so it
    /// keeps the ones worth showing. Sorting before capping would make it mean "the first N
    /// alphabetically" and silently drop the end of the alphabet.</para>
    /// </summary>
    /// <param name="byUsage">Candidates, most-used first — the order the catalog returns.</param>
    public static IReadOnlyList<KeywordUsage> Arrange(IEnumerable<KeywordUsage> byUsage, int cap)
        => byUsage
            .Take(cap)
            .OrderBy(keyword => keyword.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
