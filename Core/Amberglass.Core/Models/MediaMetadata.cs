using System.Collections.Immutable;

namespace Amberglass.Core.Models;

/// <summary>A single raw metadata tag as reported by the metadata engine, e.g. <c>XMP:Subject</c>.</summary>
public sealed record RawMetadataTag(string Group, string Name, string Value)
{
    public string QualifiedName => $"{Group}:{Name}";
}

/// <summary>
/// Everything the application knows about one file's metadata, before user edits are applied.
///
/// The embedded and sidecar layers are kept <b>separate</b> rather than pre-merged. Phase 2 only
/// displays <see cref="Effective"/>, but keeping both is what makes conflict detection possible in
/// Phase 3 without another redesign.
/// </summary>
public sealed record MediaMetadata
{
    public static readonly MediaMetadata Empty = new();

    /// <summary>User-editable metadata carried inside the media file itself.</summary>
    public EditableMetadata Embedded { get; init; } = EditableMetadata.Empty;

    /// <summary>User-editable metadata from the XMP sidecar, or null when there is no sidecar.</summary>
    public EditableMetadata? Sidecar { get; init; }

    public CameraInfo Camera { get; init; } = CameraInfo.Empty;

    public VideoInfo Video { get; init; } = VideoInfo.Empty;

    /// <summary>
    /// The picture's size the right way up, or null when the file does not say.
    ///
    /// Separate from <see cref="Camera"/>, whose values are ExifTool's formatted strings meant for
    /// display. These are numbers because something has to compare them.
    /// </summary>
    public ImageDimensions? Dimensions { get; init; }

    /// <summary>Every tag the engine reported, for the advanced raw view.</summary>
    public ImmutableArray<RawMetadataTag> RawTags { get; init; } = [];

    /// <summary>Path of the XMP sidecar backing <see cref="Sidecar"/>, when one exists.</summary>
    public string? SidecarPath { get; init; }

    public bool HasSidecar => SidecarPath is not null;

    /// <summary>
    /// When the file's own metadata last changed, and when the sidecar's did.
    ///
    /// These are what tell an ordinary non-destructive override from an external edit, which are
    /// otherwise the same thing on disk: a sidecar that disagrees with its file is the normal state
    /// of every file this application has ever edited. Only the file changing *after* the sidecar
    /// says somebody else has been here.
    ///
    /// <c>xmp:MetadataDate</c> where both sides carry it, because it moves only when metadata moves —
    /// re-exporting or rotating a JPEG leaves it alone where a file timestamp would not. Falling back
    /// to the file timestamps, which are always there.
    /// </summary>
    public DateTimeOffset? FileMetadataChangedUtc { get; init; }

    public DateTimeOffset? SidecarMetadataChangedUtc { get; init; }

    /// <summary>
    /// What the user sees before their own pending edits: the sidecar takes precedence, because it
    /// is the working representation this application writes to.
    /// </summary>
    public EditableMetadata Effective => Embedded.MergeWith(Sidecar);
}
