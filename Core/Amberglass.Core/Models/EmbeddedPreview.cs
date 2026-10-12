namespace Amberglass.Core.Models;

/// <summary>
/// The JPEG a camera embedded inside a RAW file, together with what the RAW says about which way up
/// it goes.
/// </summary>
/// <remarks>
/// The orientation has to travel with the bytes because the preview usually does not carry its own.
/// A Canon CR2's <c>PreviewImage</c> is a bare JPEG with no EXIF at all: the pixels come off the
/// sensor in the sensor's own landscape, and the only record that the camera was held on its side is
/// the orientation tag in the RAW around it. Handing the preview to an image library on its own
/// therefore produces a sideways photograph, however correct that library's orientation handling is.
/// </remarks>
/// <param name="Jpeg">The encoded preview.</param>
/// <param name="ExifOrientation">
/// The container's EXIF orientation as a number, 1 to 8, or null when the RAW does not say. Numeric
/// rather than ExifTool's formatted wording because the eight values are a fixed, standard set that
/// imaging libraries number the same way, so no second convention has to be kept in step.
/// </param>
/// <param name="SourceWidth">The RAW's stored width, before any rotation.</param>
/// <param name="SourceHeight">The RAW's stored height, before any rotation.</param>
public sealed record EmbeddedPreview(
    byte[] Jpeg,
    int? ExifOrientation = null,
    int? SourceWidth = null,
    int? SourceHeight = null);
