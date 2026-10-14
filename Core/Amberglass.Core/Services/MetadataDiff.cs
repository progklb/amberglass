using System.Collections.Immutable;
using System.Text;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>One field that differs between two readings of a file's metadata.</summary>
/// <param name="Field">The field's name, as <see cref="MetadataConflictDetector"/> names it.</param>
/// <param name="Was">The value before, or null for "not present".</param>
/// <param name="Now">The value after, or null for "not present".</param>
public sealed record FieldChange(string Field, string? Was, string? Now);

/// <summary>
/// What changed between two readings of a file's metadata, field by field.
///
/// The pending-change store already holds both sides of every edit; this is the reading of them a
/// person can review. Only fields that differ are listed, because a file whose rating changed has
/// one thing to say, not nine. Keywords are a list and get a list's diff — what was added and what
/// was removed — rather than two walls of words to compare by eye.
///
/// Null is a value here, not an absence of one. A field that went from "not present" to a word
/// is a change worth showing, and so is the reverse; the store makes the same distinction.
/// </summary>
public sealed record MetadataDiff
{
    public const string FlagField = "Flag";

    public static readonly MetadataDiff Empty = new();

    public ImmutableArray<FieldChange> Fields { get; init; } = [];

    public ImmutableArray<string> KeywordsAdded { get; init; } = [];

    public ImmutableArray<string> KeywordsRemoved { get; init; } = [];

    public bool HasKeywordChanges => !KeywordsAdded.IsDefaultOrEmpty || !KeywordsRemoved.IsDefaultOrEmpty;

    public bool IsEmpty => Fields.IsDefaultOrEmpty && !HasKeywordChanges;

    /// <summary>
    /// One line naming what changed, for a list that is scanned rather than read:
    /// "rating · label · +3 −1 keywords".
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(Fields.Length + 1);
            parts.AddRange(Fields.Select(f => f.Field.ToLowerInvariant()));

            if (HasKeywordChanges)
            {
                var keywords = new StringBuilder();
                if (!KeywordsAdded.IsDefaultOrEmpty)
                {
                    keywords.Append('+').Append(KeywordsAdded.Length);
                }

                if (!KeywordsRemoved.IsDefaultOrEmpty)
                {
                    if (keywords.Length > 0)
                    {
                        keywords.Append(' ');
                    }

                    keywords.Append('−').Append(KeywordsRemoved.Length);
                }

                parts.Add(keywords.Append(" keyword").Append(KeywordsAdded.Length + KeywordsRemoved.Length == 1 ? "" : "s").ToString());
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The differences from <paramref name="was"/> to <paramref name="now"/>, in the order the
    /// inspector lays the fields out, so the review reads in the same order as the editing did.
    /// </summary>
    public static MetadataDiff Between(EditableMetadata was, EditableMetadata now)
    {
        var fields = ImmutableArray.CreateBuilder<FieldChange>();

        Add(MetadataConflictDetector.TitleField, was.Title, now.Title);
        Add(MetadataConflictDetector.HeadlineField, was.Headline, now.Headline);
        Add(MetadataConflictDetector.DescriptionField, was.Description, now.Description);
        Add(MetadataConflictDetector.RatingField, Describe(was.Rating), Describe(now.Rating));
        Add(FlagField, Describe(was.Flag), Describe(now.Flag));
        Add(MetadataConflictDetector.LabelField, was.Label, now.Label);
        Add(MetadataConflictDetector.CreatorField, was.Creator, now.Creator);
        Add(MetadataConflictDetector.CopyrightField, was.Copyright, now.Copyright);

        var before = was.Keywords.IsDefault ? [] : was.Keywords;
        var after = now.Keywords.IsDefault ? [] : now.Keywords;

        // Ordinal on purpose: the writer stores what it is given, so "Sand" and "sand" are two
        // keywords to the file even if they are one to the reader.
        var added = after.Where(k => !before.Contains(k, StringComparer.Ordinal)).ToImmutableArray();
        var removed = before.Where(k => !after.Contains(k, StringComparer.Ordinal)).ToImmutableArray();

        return new MetadataDiff
        {
            Fields = fields.ToImmutable(),
            KeywordsAdded = added,
            KeywordsRemoved = removed
        };

        void Add(string field, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                fields.Add(new FieldChange(field, before, after));
            }
        }
    }

    /// <summary>
    /// What the sidecar says that the file itself does not: the file's own values on one side, the
    /// values in use (the sidecar laid over the file) on the other. Empty without a sidecar.
    ///
    /// This is what embedding would change, and what an application that ignores sidecars is
    /// missing. Fields the sidecar is silent on fall through to the file's own value, so they never
    /// show up as a difference.
    /// </summary>
    public static MetadataDiff SidecarOverFile(MediaMetadata metadata)
    {
        if (metadata.Sidecar is null)
        {
            return Empty;
        }

        var file = metadata.Embedded;
        var inUse = metadata.Effective;

        // A sidecar saying "unrated" to a file that says nothing is the same answer twice.
        if (file.Rating is null && inUse.Rating == 0)
        {
            inUse = inUse with { Rating = null };
        }

        return Between(file, inUse);
    }

    /// <summary>
    /// A rating as words rather than as a number that could be mistaken for a count. Zero is
    /// "Unrated" — a file can carry an explicit zero, which is not the same as carrying nothing.
    /// </summary>
    public static string? Describe(int? rating) => rating switch
    {
        null => null,
        0 => "Unrated",
        1 => "1 star",
        var n => $"{n} stars"
    };

    /// <summary>
    /// A flag in the words the interface uses. An explicit None is "Cleared" rather than nothing:
    /// it is a decision somebody recorded, and the store keeps it apart from no flag at all.
    /// </summary>
    public static string? Describe(MediaFlag? flag) => flag switch
    {
        null => null,
        MediaFlag.Accepted => "Keep",
        MediaFlag.Rejected => "Rejected",
        MediaFlag.Pending => "Pending",
        _ => "Cleared"
    };
}
