using System.Text;

namespace Amberglass.Core.Services;

/// <summary>
/// Edits one field inside a typed query, leaving the rest of it alone.
///
/// This is what lets the filter controls write into the search box rather than keep a filter state
/// of their own. There is then one query, visible and editable, and clicking three stars teaches the
/// user that it is spelled <c>r:&gt;=3</c> — which is the point of having short forms at all. A
/// parallel set of filters would have needed reconciling with the text every time either changed.
/// </summary>
public static class SearchQueryText
{
    /// <summary>
    /// Returns <paramref name="text"/> with <paramref name="field"/> set to
    /// <paramref name="value"/>, replacing any existing term for that field, or removing it when
    /// the value is null.
    ///
    /// The field is written in its short form: the box is the only place the syntax is on show, so
    /// it may as well show the form worth learning.
    /// </summary>
    public static string WithField(string? text, string field, string? value)
    {
        var canonical = SearchFields.Resolve(field)
            ?? throw new ArgumentException($"'{field}' is not a search field.", nameof(field));

        var shortForm = SearchFields.All.First(f => f.Name == canonical).Short;
        var rebuilt = Without(text, canonical);

        if (!string.IsNullOrWhiteSpace(value))
        {
            Append(rebuilt, $"{shortForm}:{Quote(value)}");
        }

        return rebuilt.ToString();
    }

    /// <summary>
    /// Sets a field to several separate terms, replacing whatever it had.
    ///
    /// Separate terms are how "all of these" is written — <c>k:sand k:dust</c> — where one term with
    /// commas means "any of these". Both are needed, so this exists alongside
    /// <see cref="WithField"/> rather than instead of it.
    /// </summary>
    public static string WithFieldTerms(string? text, string field, IEnumerable<string> values)
    {
        var canonical = SearchFields.Resolve(field)
            ?? throw new ArgumentException($"'{field}' is not a search field.", nameof(field));

        var shortForm = SearchFields.All.First(f => f.Name == canonical).Short;
        var rebuilt = Without(text, canonical);

        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            Append(rebuilt, $"{shortForm}:{Quote(value)}");
        }

        return rebuilt.ToString();
    }

    /// <summary>
    /// The query with <paramref name="canonical"/> removed, and half-typed terms tidied away.
    /// </summary>
    private static StringBuilder Without(string? text, string canonical)
    {
        var rebuilt = new StringBuilder();

        foreach (var token in SearchQueryParser.Tokenize(text ?? string.Empty))
        {
            var separator = token.IndexOf(':');

            // Any spelling of the same field goes, so setting a value never leaves an older term
            // for it further along the query still filtering.
            if (separator > 0 && SearchFields.Resolve(token[..separator]) == canonical)
            {
                continue;
            }

            if (IsHalfTyped(token))
            {
                continue;
            }

            Append(rebuilt, token);
        }

        return rebuilt;
    }

    /// <summary>
    /// A term with a colon and nothing after it: <c>:</c> on its own, or <c>k:</c> with no value yet.
    ///
    /// These are what someone leaves behind when they start typing a filter, think better of it, and
    /// go to the filter panel instead. They cannot filter anything — a bare <c>:</c> is worse than
    /// nothing, since it becomes a free-text term that matches no file and empties the results — so
    /// a control writing on the user's behalf clears them rather than building on top of them.
    ///
    /// Only these. Ordinary free text stays: <c>bush k:Bush</c> is a real query, and deciding that
    /// clicking a filter should throw away what someone typed would be a much larger assumption.
    /// </summary>
    private static bool IsHalfTyped(string token)
    {
        var colon = token.IndexOf(':');

        return colon >= 0 && token[(colon + 1)..].Trim('"').Trim().Length == 0;
    }

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(token);
    }

    /// <summary>A value with a space in it has to come back quoted or it tokenizes as two terms.</summary>
    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) && !value.StartsWith('"') ? $"\"{value}\"" : value;
}
