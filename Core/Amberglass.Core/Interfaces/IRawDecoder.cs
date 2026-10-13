using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>Finds LibRaw's command line tool, the way ExifTool and FFmpeg are found.</summary>
public interface ILibRawLocator
{
    string? DcrawPath { get; }

    bool IsAvailable { get; }
}

/// <summary>
/// Develops a RAW file into viewable pixels — demosaicing the sensor data rather than reading the
/// JPEG the camera embedded alongside it.
///
/// Worth the trouble because the embedded preview is not the photograph: on a 26MP camera it is
/// typically a 13MP JPEG with the camera's processing already baked in.
/// </summary>
public interface IRawDecoder
{
    bool IsAvailable { get; }

    /// <summary>Null when the file is not RAW, LibRaw is missing, or development fails.</summary>
    Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// A quick, reduced-resolution develop with the same settings — what is shown while a develop
    /// slider is still moving, so the change can be seen before the full develop has been paid for.
    ///
    /// Null when the decoder has no cheaper route than the full develop, which is the default: the
    /// caller then simply waits for <see cref="DevelopAsync"/>.
    /// </summary>
    Task<DecodedImage?> DevelopDraftAsync(MediaFile file, CancellationToken cancellationToken = default)
        => Task.FromResult<DecodedImage?>(null);
}
