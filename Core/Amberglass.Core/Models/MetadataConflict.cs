namespace Amberglass.Core.Models;

/// <summary>
/// One editable field whose embedded value disagrees with the XMP sidecar. Both sides are kept so
/// the user can see exactly what they are choosing between — the application never picks silently.
/// </summary>
public sealed record MetadataConflict(string Field, string? EmbeddedValue, string? SidecarValue);

/// <summary>How the user chose to settle a set of conflicts.</summary>
public enum ConflictResolution
{
    /// <summary>Take the value inside the media file.</summary>
    KeepEmbedded,

    /// <summary>Take the value from the sidecar.</summary>
    KeepSidecar,

    /// <summary>
    /// Union the keyword lists; for single-valued fields the sidecar wins, since it is the layer this
    /// application writes to.
    /// </summary>
    Merge
}
