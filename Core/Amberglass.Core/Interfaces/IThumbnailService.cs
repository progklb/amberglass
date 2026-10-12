using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// Why a thumbnail was asked for. Interactive work must not wait behind speculative work.
/// </summary>
public enum ThumbnailPriority
{
    /// <summary>Filling the grid. Plenty of these, and nobody is staring at any one of them.</summary>
    Background,

    /// <summary>The user selected this file and is waiting for it.</summary>
    Interactive
}

/// <summary>
/// Produces cached thumbnail images. Returns encoded image bytes rather than a UI bitmap type so
/// that the preview layer stays independent of the UI toolkit.
/// </summary>
public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailAsync(
        MediaFile file,
        int maxEdgePixels,
        ThumbnailPriority priority = ThumbnailPriority.Background,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates a thumbnail for a single class of media. Registered implementations are tried in order.
/// </summary>
public interface IThumbnailGenerator
{
    bool CanHandle(MediaFile file);

    Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default);
}
