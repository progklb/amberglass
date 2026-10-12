namespace Amberglass.Core.Interfaces;

/// <summary>
/// Resolves the external FFmpeg tools. FFmpeg is an optional dependency in Phase 1: when it is
/// absent the application still browses video files, it just cannot render frame thumbnails.
/// </summary>
public interface IFfmpegLocator
{
    bool IsAvailable { get; }

    string? FfmpegPath { get; }

    string? FfprobePath { get; }
}
