using Amberglass.Core.Services;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One row of the search box's suggestion list.
///
/// Fields and values share a type so the popup has one template. They differ only in what the two
/// columns hold — a field shows its short form and what it matches, a value shows the word and how
/// many files carry it.
/// </summary>
/// <param name="Lead">The left column: a short form for a field, a count for a value.</param>
/// <param name="Text">What is inserted, and the main thing read.</param>
/// <param name="Detail">The quieter line beneath.</param>
/// <param name="IsField">Whether accepting this writes a field name or a value.</param>
public sealed record SearchSuggestionItem(string Lead, string Text, string Detail, bool IsField)
{
    public static SearchSuggestionItem ForField(SearchField field)
        => new($"{field.Short}:", $"{field.Name}:", field.Summary, IsField: true);

    /// <param name="count">
    /// How many files carry it, or null when the value is not something the catalog counts — a
    /// media kind or a flag is a fixed choice rather than a tally.
    /// </param>
    public static SearchSuggestionItem ForValue(string value, int? count)
        => new(count is { } n ? n.ToString("N0") : string.Empty, value, string.Empty, IsField: false);
}
