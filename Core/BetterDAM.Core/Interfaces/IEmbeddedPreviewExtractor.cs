using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

/// <summary>
/// Pulls the JPEG preview that cameras embed inside RAW files.
///
/// RAW formats cannot be decoded by the imaging library, but every camera stores a ready-made JPEG
/// alongside the sensor data — which is exactly what Bridge and Photo Mechanic display. Extracting
/// it is both the only practical way to show a RAW thumbnail and far faster than developing the RAW.
/// </summary>
public interface IEmbeddedPreviewExtractor
{
    bool IsAvailable { get; }

    /// <summary>
    /// The preview and the orientation to show it at, or null when the file carries no usable
    /// preview. The orientation comes from the RAW rather than the preview because the preview
    /// commonly has no EXIF of its own; see <see cref="EmbeddedPreview"/>.
    /// </summary>
    Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default);
}
