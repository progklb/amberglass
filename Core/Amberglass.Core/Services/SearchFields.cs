using System.Collections.Immutable;

namespace Amberglass.Core.Services;

/// <summary>
/// One searchable field, described once.
/// </summary>
/// <param name="Name">The canonical name, and what the parser switches on.</param>
/// <param name="Short">The single letter a practised user types. Shown in the help.</param>
/// <param name="Summary">What it matches, in the fewest words that are still true.</param>
/// <param name="Example">A query that works, used in the help and in the suggestion list.</param>
/// <param name="AlsoAccepts">
/// Spellings kept working but not advertised — an alias that once shipped cannot be withdrawn
/// without breaking someone's saved habit.
/// </param>
public sealed record SearchField(
    string Name,
    string Short,
    string Summary,
    string Example,
    ImmutableArray<string> AlsoAccepts = default)
{
    public IEnumerable<string> AllSpellings
    {
        get
        {
            yield return Name;
            yield return Short;

            foreach (var alias in AlsoAccepts.IsDefault ? [] : AlsoAccepts)
            {
                yield return alias;
            }
        }
    }
}

/// <summary>
/// The search vocabulary, in one place.
///
/// The parser, the help in the filter popup and the list offered when a colon is typed all read from
/// this. Stated once because the alternative is three lists that agree on the day they are written
/// and quietly stop agreeing afterwards — the sort of drift where a field works but is undocumented,
/// or is offered by the UI and then rejected by the parser.
/// </summary>
public static class SearchFields
{
    /// <summary>
    /// Every field here must be one the parser understands — asserted by a test, which is what
    /// caught colour label being advertised before <c>SearchQuery</c> had anywhere to put it.
    /// </summary>
    public static ImmutableArray<SearchField> All { get; } =
    [
        new("keyword", "k", "Files tagged with a word", "k:sand,dust", ["kw"]),
        new("rating", "r", "Stars, with > < >= <=", "r:>=4"),
        new("type", "t", "raw, jpg or video", "t:raw,video"),
        new("orientation", "o", "portrait, landscape or square", "o:portrait"),
        new("label", "lb", "Colour label, or none", "lb:select,none"),
        new("flag", "f", "accepted, rejected or none", "f:accepted"),
        new("filename", "fn", "Part of the file name", "fn:DSCF7676"),
        new("camera", "c", "Camera make or model", "c:Fujifilm"),
        new("lens", "l", "Lens name; quote it if it has spaces", "l:\"RF 100-500\""),
        new("date", "d", "Capture date, or a bare year", "d:>=2024-01-01")
    ];

    private static readonly ImmutableDictionary<string, string> Lookup = BuildLookup();

    /// <summary>
    /// Maps any accepted spelling to its canonical name, or null when the word is not a field at
    /// all — which the parser treats as free text rather than as an error.
    /// </summary>
    public static string? Resolve(string field)
        => Lookup.TryGetValue(field, out var name) ? name : null;

    private static ImmutableDictionary<string, string> BuildLookup()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in All)
        {
            foreach (var spelling in field.AllSpellings)
            {
                // Deliberately throwing rather than last-one-wins: two fields claiming one spelling
                // is a mistake that would otherwise show up as a search silently filtering by the
                // wrong thing.
                if (builder.ContainsKey(spelling))
                {
                    throw new InvalidOperationException(
                        $"Search field spelling '{spelling}' is claimed by more than one field.");
                }

                builder[spelling] = field.Name;
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// The fields worth offering for a partly typed name. An empty prefix offers everything, which
    /// is what a bare colon means: "remind me what there is".
    /// </summary>
    public static IEnumerable<SearchField> Matching(string? prefix)
        => string.IsNullOrEmpty(prefix)
            ? All
            : All.Where(field => field.AllSpellings.Any(
                spelling => spelling.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
}
