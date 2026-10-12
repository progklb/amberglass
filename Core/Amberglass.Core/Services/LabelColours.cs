using System.Collections.Immutable;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>
/// Works out what colour to draw a label in.
///
/// The difficulty is the one <see cref="LabelLibrary"/> describes: <c>xmp:Label</c> is a string, and
/// every application picks its own colour for a given word. A file labelled in Lightroom arrives
/// here saying "Yellow" and nothing else — no colour, and nothing in this user's library to match it
/// against if they have kept Bridge's names.
///
/// So there are three answers, in order of how much they are worth trusting:
///
/// <list type="number">
/// <item>the user's own library, which is the only place a colour was deliberately chosen;</item>
/// <item>the word itself, when it names a colour — "Yellow" is not a guess, it is what the label
/// says, and refusing to read it would be perverse;</item>
/// <item>grey, for a word that means something to whoever wrote it and nothing here.</item>
/// </list>
///
/// Grey rather than nothing, because the file <em>is</em> labelled and a tile showing no mark at all
/// would say the opposite.
/// </summary>
public static class LabelColours
{
    /// <summary>
    /// A label that names no colour and is not in the library. It still gets a mark, because the
    /// alternative is a labelled file that looks unlabelled.
    /// </summary>
    public const string Unrecognised = "#8C8C8C";

    /// <summary>
    /// Colour words, in the shades this application already uses for the same colours.
    ///
    /// Lightroom's default label set is exactly the first five. They are here so that a workspace
    /// labelled in Lightroom, or by anyone who names labels after colours, colours itself correctly
    /// without the user having to rebuild their library to match.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> ByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Red"] = "#E8574A",
            ["Yellow"] = "#E8C84A",
            ["Green"] = "#6ABF52",
            ["Blue"] = "#4AA3E8",
            ["Purple"] = "#B77BD8",

            // Not Adobe's, but people do use them.
            ["Orange"] = "#E8944A",
            ["Pink"] = "#E87AA8",
            ["Teal"] = "#4AC8C0",
            ["Cyan"] = "#4AC8C0",
            ["Magenta"] = "#D14AC8",
            ["Brown"] = "#A6785A",
            ["Grey"] = "#9A9A9A",
            ["Gray"] = "#9A9A9A",
            ["White"] = "#E6E6E6",
            ["Black"] = "#3A3A3A"
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The colour for a label, or null when there is no label.
    ///
    /// Never null for a label that exists: an unrecognised word still gets
    /// <see cref="Unrecognised"/>, since the point of the mark is to say the file is labelled.
    /// </summary>
    public static string? Resolve(LabelLibrary? library, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var name = label.Trim();

        // The library first: if the user has named a label "Yellow" and coloured it blue, that is a
        // deliberate choice and it outranks what the word happens to mean.
        return library?.Find(name)?.Colour
            ?? (ByName.TryGetValue(name, out var known) ? known : Unrecognised);
    }

    /// <summary>
    /// True when the colour came from the word rather than the library — the case worth explaining
    /// in a tooltip, since the label is one this workspace uses but the library has never heard of.
    /// </summary>
    public static bool IsFromTheWord(LabelLibrary? library, string? label)
        => library?.Find(label?.Trim() ?? string.Empty) is null && NamesAColour(label);

    /// <summary>
    /// True when the word itself names a colour this application knows, whatever the library says.
    ///
    /// Separate from <see cref="IsFromTheWord"/>, which asks whether the colour on screen came from
    /// the word; this asks only whether the word offers one. The label editor uses it to colour a
    /// row as its name is typed.
    /// </summary>
    public static bool NamesAColour(string? label)
        => !string.IsNullOrWhiteSpace(label) && ByName.ContainsKey(label.Trim());
}
