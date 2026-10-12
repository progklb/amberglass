namespace Amberglass.Core.Services;

/// <summary>What the search box should be offering at the caret.</summary>
public enum SuggestionKind
{
    None,

    /// <summary>Field names — the user is still deciding what to filter by.</summary>
    Field,

    /// <summary>Values for a field already named.</summary>
    Value
}

/// <param name="Field">The canonical field name, for <see cref="SuggestionKind.Value"/>.</param>
/// <param name="Prefix">What has been typed so far, which the offer is narrowed by.</param>
public readonly record struct SuggestionRequest(SuggestionKind Kind, string Field, string Prefix)
{
    public static readonly SuggestionRequest Nothing = new(SuggestionKind.None, string.Empty, string.Empty);
}

/// <summary>
/// Decides what the search box should offer at the caret, and rewrites the text when something is
/// chosen.
///
/// Kept out of the view because it is entirely string work and the interesting cases — a colon in
/// the middle of a word, a second colon, a comma-separated list half typed, a caret that is not at
/// the end — are far easier to state as tests than to reproduce by typing into a window.
/// </summary>
public static class SearchSuggestion
{
    /// <summary>
    /// What to offer, if anything.
    ///
    /// The rule is one line: <b>if the word before the colon is a field, offer its values;
    /// otherwise offer field names.</b> So <c>:</c> lists the fields, <c>key:</c> narrows them to
    /// keyword, and <c>k:</c> — which is already a field — moves straight on to offering keywords.
    /// </summary>
    public static SuggestionRequest At(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return SuggestionRequest.Nothing;
        }

        var start = TokenStart(text, caret);
        var segment = text[start..caret];

        var colon = segment.IndexOf(':');
        if (colon < 0)
        {
            return SuggestionRequest.Nothing;
        }

        var field = segment[..colon];
        var value = segment[(colon + 1)..];

        if (SearchFields.Resolve(field) is { } canonical)
        {
            // Commas separate alternatives, so only the one being typed is completed.
            var lastComma = value.LastIndexOf(',');
            return new SuggestionRequest(SuggestionKind.Value, canonical, value[(lastComma + 1)..]);
        }

        // Not a field yet. Offer field names, but only while nothing has been typed after the colon —
        // by then it is a word with a colon in it, like a URL, and not a filter at all.
        return value.Length == 0
            ? new SuggestionRequest(SuggestionKind.Field, string.Empty, field)
            : SuggestionRequest.Nothing;
    }

    /// <summary>
    /// Replaces the field being typed with <paramref name="field"/>, leaving the caret after the
    /// colon, ready for the value.
    /// </summary>
    public static (string Text, int Caret) AcceptField(string? text, int caret, string field)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        var start = TokenStart(text, caret);
        var replacement = field + ":";

        return (text[..start] + replacement + text[caret..], start + replacement.Length);
    }

    /// <summary>
    /// Replaces the value being typed with <paramref name="value"/>, keeping any earlier
    /// alternatives in the same term.
    /// </summary>
    public static (string Text, int Caret) AcceptValue(string? text, int caret, string value)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        var start = TokenStart(text, caret);
        var segment = text[start..caret];

        var colon = segment.IndexOf(':');
        if (colon < 0)
        {
            return (text, caret);
        }

        // Everything up to and including the last comma stays: "k:sand,du" keeps "k:sand,".
        var lastComma = segment.LastIndexOf(',');
        var keep = start + Math.Max(colon, lastComma) + 1;

        var replacement = Quote(value);

        return (text[..keep] + replacement + text[caret..], keep + replacement.Length);
    }

    /// <summary>A value with a space has to come back quoted, or it tokenizes as two terms.</summary>
    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) && !value.StartsWith('"') ? $"\"{value}\"" : value;

    private static int TokenStart(string text, int from)
    {
        var start = from;

        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        return start;
    }
}
