using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// Reads metadata for a media file. The rest of the application depends on this rather than on
/// ExifTool, so an alternative engine — or a cached/database-backed one — can be substituted later.
///
/// Phase 2 is read-only by design. Writing arrives with XMP sidecars in Phase 3 and embedding in
/// Phase 6, which is why there is no Write method here yet.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>True when the underlying engine is usable. False degrades the UI gracefully.</summary>
    bool IsAvailable { get; }

    Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads many files, keyed by full path. Files that could not be read are simply absent.
    ///
    /// This exists because batch editing needs each file's current metadata as the baseline for its
    /// pending change, and doing that one file at a time does not scale to a thousand-file
    /// selection — a metadata engine can answer for many files in a single round trip.
    /// </summary>
    Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the external ExifTool executable.</summary>
public interface IExifToolLocator
{
    bool IsAvailable { get; }

    string? ExifToolPath { get; }
}
