using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>Reads duration, dimensions and frame rate — everything a timeline needs.</summary>
public interface IVideoInfoProvider
{
    bool IsAvailable { get; }

    Task<VideoMediaInfo?> GetInfoAsync(MediaFile file, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates and caches low-resolution stand-ins for source video.
/// </summary>
public interface IVideoProxyService
{
    bool IsAvailable { get; }

    /// <summary>True when a proxy for this quality already exists, so the UI can say so without work.</summary>
    bool HasProxy(MediaFile file, VideoQuality quality);

    /// <summary>
    /// Returns the existing proxy or generates one. Progress is reported 0–1 while encoding.
    /// <see cref="VideoQuality.Original"/> resolves to the source file itself and never encodes.
    /// </summary>
    Task<VideoProxy?> GetProxyAsync(
        MediaFile file,
        VideoQuality quality,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Decodes video to raw frames for on-screen playback.
/// </summary>
public interface IVideoFrameSource
{
    bool IsAvailable { get; }

    /// <summary>
    /// Streams frames from <paramref name="position"/> onwards. The consumer must dispose each frame
    /// to return its buffer to the pool; abandoning them will exhaust the pool under playback rates.
    /// </summary>
    IAsyncEnumerable<VideoFrame> StreamAsync(
        string path,
        VideoMediaInfo info,
        TimeSpan position,
        CancellationToken cancellationToken = default);

    /// <summary>A single frame for scrub feedback, where a whole stream would be wasteful.</summary>
    Task<VideoFrame?> GetFrameAsync(
        string path,
        VideoMediaInfo info,
        TimeSpan position,
        CancellationToken cancellationToken = default);
}
