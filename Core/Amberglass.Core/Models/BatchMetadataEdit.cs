using System.Collections.Immutable;

namespace Amberglass.Core.Models;

/// <summary>
/// A metadata change to apply across many files.
///
/// Every field is opt-in. Editing hundreds of files at once is the operation with the most potential
/// to destroy work, so a field is only touched when its <c>Apply…</c> flag is set — a blank box means
/// "leave this alone", never "clear this everywhere".
///
/// Keywords default to <b>adding and removing</b> rather than replacing, because a shared keyword
/// list across a mixed selection is rarely what someone means. Replacing is available, but explicit.
/// </summary>
public sealed record BatchMetadataEdit
{
    public bool ApplyTitle { get; init; }

    public string? Title { get; init; }

    public bool ApplyHeadline { get; init; }

    public string? Headline { get; init; }

    public bool ApplyDescription { get; init; }

    public string? Description { get; init; }

    public bool ApplyRating { get; init; }

    /// <summary>Null clears the rating, which is only reachable with <see cref="ApplyRating"/> set.</summary>
    public int? Rating { get; init; }

    public bool ApplyLabel { get; init; }

    public string? Label { get; init; }

    public bool ApplyCreator { get; init; }

    public string? Creator { get; init; }

    public bool ApplyCopyright { get; init; }

    public string? Copyright { get; init; }

    public ImmutableArray<string> KeywordsToAdd { get; init; } = [];

    public ImmutableArray<string> KeywordsToRemove { get; init; } = [];

    /// <summary>Discards each file's existing keywords in favour of <see cref="ReplacementKeywords"/>.</summary>
    public bool ReplaceKeywords { get; init; }

    public ImmutableArray<string> ReplacementKeywords { get; init; } = [];

    public bool HasAnyChange =>
        ApplyTitle || ApplyHeadline || ApplyDescription || ApplyRating ||
        ApplyLabel || ApplyCreator || ApplyCopyright ||
        ReplaceKeywords || !KeywordsToAdd.IsDefaultOrEmpty || !KeywordsToRemove.IsDefaultOrEmpty;

    /// <summary>
    /// Produces the edited metadata for one file. Pure — no I/O, no shared state — so the batch
    /// semantics can be tested exhaustively without touching a disk.
    /// </summary>
    public EditableMetadata ApplyTo(EditableMetadata original) => original with
    {
        Title = ApplyTitle ? NullIfBlank(Title) : original.Title,
        Headline = ApplyHeadline ? NullIfBlank(Headline) : original.Headline,
        Description = ApplyDescription ? NullIfBlank(Description) : original.Description,
        Rating = ApplyRating ? Rating : original.Rating,
        Label = ApplyLabel ? NullIfBlank(Label) : original.Label,
        Creator = ApplyCreator ? NullIfBlank(Creator) : original.Creator,
        Copyright = ApplyCopyright ? NullIfBlank(Copyright) : original.Copyright,
        Keywords = ApplyKeywords(original.Keywords)
    };

    private ImmutableArray<string> ApplyKeywords(ImmutableArray<string> existing)
    {
        if (ReplaceKeywords)
        {
            return Normalise(ReplacementKeywords);
        }

        if (KeywordsToAdd.IsDefaultOrEmpty && KeywordsToRemove.IsDefaultOrEmpty)
        {
            return existing;
        }

        var result = existing.IsDefaultOrEmpty ? [] : existing.ToList();

        if (!KeywordsToRemove.IsDefaultOrEmpty)
        {
            result.RemoveAll(k => KeywordsToRemove.Contains(k, StringComparer.OrdinalIgnoreCase));
        }

        if (!KeywordsToAdd.IsDefaultOrEmpty)
        {
            foreach (var keyword in KeywordsToAdd)
            {
                // Case-insensitive so adding "Namibia" to a file that already has "namibia" does not
                // produce a near-duplicate pair.
                if (!result.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(keyword);
                }
            }
        }

        return [.. result];
    }

    private static ImmutableArray<string> Normalise(ImmutableArray<string> keywords)
        => keywords.IsDefaultOrEmpty
            ? []
            : [.. keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
