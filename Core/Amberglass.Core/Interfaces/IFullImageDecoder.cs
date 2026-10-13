using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// An image decoded at full resolution, as tightly packed BGRA ready to blit.
///
/// Raw pixels rather than encoded bytes: this exists to avoid a lossy round-trip, so re-encoding it
/// to hand it over would defeat the point. Large — a 24MP frame is around 96 MB — so it is loaded on
/// demand and released as soon as something else is being looked at.
/// </summary>
/// <param name="Renderer">
/// What produced the pixels, for the UI to be honest about. Only a LibRaw develop answers to the
/// develop settings; anything else is that renderer's own interpretation of the file.
/// </param>
public sealed record DecodedImage(byte[] Pixels, int Width, int Height, string? Renderer = null)
{
    public const string LibRaw = "LibRaw";
    public const string Platform = "macOS";

    public long SizeBytes => (long)Width * Height * 4;
}

/// <summary>
/// Decodes an image at its native resolution, for inspection rather than browsing.
///
/// Separate from <see cref="IThumbnailService"/> on purpose: thumbnails are deliberately small and
/// JPEG-compressed, which is right for a grid of hundreds and wrong for judging a photograph.
/// </summary>
public interface IFullImageDecoder
{
    Task<DecodedImage?> DecodeAsync(MediaFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// A quick reduced-resolution rendering with the current develop settings, to show while those
    /// settings are still being adjusted. Null when there is nothing quicker than
    /// <see cref="DecodeAsync"/> — anything that is not a RAW being developed, for a start.
    /// </summary>
    Task<DecodedImage?> DecodeDraftAsync(MediaFile file, CancellationToken cancellationToken = default)
        => Task.FromResult<DecodedImage?>(null);
}
