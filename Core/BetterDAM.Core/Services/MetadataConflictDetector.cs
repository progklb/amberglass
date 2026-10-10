using System.Collections.Immutable;
using BetterDAM.Core.Models;

namespace BetterDAM.Core.Services;

/// <summary>
/// Compares the file's own values with its sidecar's, field by field.
///
/// A conflict requires <b>both</b> sides to carry a value and for those values to differ. A field the
/// sidecar simply does not mention is not a conflict — that is the normal case for a sidecar that
/// only carries a rating.
///
/// Three things are not conflicts, and saying why matters more than the code does:
///
/// <list type="number">
/// <item>
/// <b>A rating of zero.</b> Cameras write <c>Rating = 0</c> into every file they produce — measured
/// across RAF, CR2, JPG and Lightroom's DNG, every single one. It means unrated. Reading it as a
/// value made the first star awarded to any file disagree with the file it was awarded to, which is
/// the application flagging its own work.
/// </item>
/// <item>
/// <b>A proprietary raw file disagreeing with its sidecar.</b> Nothing writes into a CR2 or an NEF —
/// not this application and not Lightroom, Camera Raw or Bridge. The sidecar is the record for those
/// formats, and the file keeping the values the camera gave it is the arrangement working.
/// </item>
/// <item>
/// <b>A sidecar newer than its file.</b> That is this application's own last word, and the ordinary
/// state of every file it has edited. Only the file changing afterwards means somebody else has been
/// here — which is the case worth asking about, and the only one a person can usefully answer.
/// </item>
/// </list>
/// </summary>
public static class MetadataConflictDetector
{
    public const string TitleField = "Title";
    public const string DescriptionField = "Description";
    public const string KeywordsField = "Keywords";
    public const string RatingField = "Rating";
    public const string LabelField = "Label";
    public const string CreatorField = "Creator";
    public const string CopyrightField = "Copyright";
    public const string HeadlineField = "Headline";

    /// <param name="filePath">
    /// Needed to know whether anything would ever write into this file. Optional so that callers
    /// comparing two readings in the abstract still work; without it the format is assumed open.
    /// </param>
    public static ImmutableArray<MetadataConflict> Detect(MediaMetadata metadata, string? filePath = null)
    {
        if (metadata.Sidecar is not { } sidecar)
        {
            return [];
        }

        if (filePath is not null && !MediaTypeRegistry.CanEmbedMetadata(filePath))
        {
            return [];
        }

        if (!FileChangedAfterSidecar(metadata))
        {
            return [];
        }

        var embedded = metadata.Embedded;
        var conflicts = ImmutableArray.CreateBuilder<MetadataConflict>();

        AddIfConflicting(TitleField, embedded.Title, sidecar.Title);
        AddIfConflicting(DescriptionField, embedded.Description, sidecar.Description);
        AddIfConflicting(HeadlineField, embedded.Headline, sidecar.Headline);
        AddIfConflicting(LabelField, embedded.Label, sidecar.Label);
        AddIfConflicting(CreatorField, embedded.Creator, sidecar.Creator);
        AddIfConflicting(CopyrightField, embedded.Copyright, sidecar.Copyright);
        AddIfConflicting(
            RatingField,
            RatingValue(embedded.Rating),
            RatingValue(sidecar.Rating));

        // Keyword order is not meaningful, so only membership counts.
        if (!embedded.Keywords.IsDefaultOrEmpty && !sidecar.Keywords.IsDefaultOrEmpty &&
            !embedded.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(sidecar.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            conflicts.Add(new MetadataConflict(
                KeywordsField,
                string.Join(", ", embedded.Keywords),
                string.Join(", ", sidecar.Keywords)));
        }

        return conflicts.ToImmutable();

        static string? RatingValue(int? rating) => rating is null or 0 ? null : rating.Value.ToString();

        void AddIfConflicting(string field, string? embeddedValue, string? sidecarValue)
        {
            if (embeddedValue is null || sidecarValue is null || embeddedValue == sidecarValue)
            {
                return;
            }

            conflicts.Add(new MetadataConflict(field, embeddedValue, sidecarValue));
        }
    }

    /// <summary>
    /// Whether the file's own metadata changed after the sidecar's did.
    ///
    /// Unknown counts as yes. A missing timestamp should not silence a disagreement that may be real;
    /// the worst that costs is a question, where the other way round costs a lost edit.
    /// </summary>
    internal static bool FileChangedAfterSidecar(MediaMetadata metadata)
        => metadata.FileMetadataChangedUtc is not { } file
           || metadata.SidecarMetadataChangedUtc is not { } sidecar
           || file > sidecar;

    /// <summary>
    /// Produces the metadata that settles the conflicts, ready to be recorded as a pending edit.
    /// </summary>
    public static EditableMetadata Resolve(MediaMetadata metadata, ConflictResolution resolution)
    {
        var embedded = metadata.Embedded;
        var sidecar = metadata.Sidecar;

        return resolution switch
        {
            // Only the conflicting side is replaced; fields the sidecar uniquely supplies survive.
            ConflictResolution.KeepEmbedded => (sidecar ?? EditableMetadata.Empty).MergeWith(embedded),
            ConflictResolution.KeepSidecar => metadata.Effective,
            ConflictResolution.Merge => metadata.Effective with
            {
                Keywords = embedded.Keywords
                    .Concat(sidecar?.Keywords ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray()
            },
            _ => metadata.Effective
        };
    }
}
