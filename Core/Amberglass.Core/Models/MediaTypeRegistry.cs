namespace Amberglass.Core.Models;

/// <summary>
/// Maps file extensions to <see cref="MediaType"/>. This is a starting set covering the formats
/// listed in the project README; the format list is expected to grow as ExifTool/FFmpeg coverage
/// is wired in rather than being treated as a hard application limit.
/// </summary>
public static class MediaTypeRegistry
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".srw", ".pef"
    };

    /// <summary>
    /// Sensor data rather than a finished picture. These need developing to be viewed properly;
    /// everything else in the image set is already an image.
    /// </summary>
    /// <summary>
    /// Exposed so the catalog can filter raw from rendered in SQL. The database stores no flag for
    /// it, so the query has to test the extension — and it must test the same list the rest of the
    /// application uses, not a second copy of it.
    /// </summary>
    public static IReadOnlyCollection<string> RawFileExtensions => RawExtensions;

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".srw", ".pef"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mxf", ".m4v"
    };

    public static MediaType GetMediaType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (ImageExtensions.Contains(extension))
        {
            return MediaType.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaType.Video;
        }

        return MediaType.Unsupported;
    }

    public static bool IsSupported(string filePath) => GetMediaType(filePath) != MediaType.Unsupported;

    public static bool IsRaw(string filePath) => RawExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// The proprietary raw formats, which are the ones nothing should write into.
    ///
    /// DNG is deliberately absent even though it is raw: it is a container designed to hold XMP, and
    /// Adobe's own tools write metadata straight into it. "Raw" is the wrong line to draw here — the
    /// question is whether the format is one whose vendor sanctions being written to, and for a CR2
    /// or an NEF the answer is no.
    /// </summary>
    private static readonly HashSet<string> ClosedRawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".srw", ".pef"
    };

    /// <summary>
    /// Whether metadata can be written into the file itself, rather than only beside it.
    ///
    /// False for proprietary raw. Those formats are undocumented, every camera maker's differs, and
    /// the whole industry — Lightroom, Camera Raw, Bridge — leaves them alone and keeps user metadata
    /// in a sidecar. The sidecar is the record for them, and its disagreeing with the file is the
    /// arrangement working rather than a fault.
    /// </summary>
    public static bool CanEmbedMetadata(string filePath)
        => !ClosedRawExtensions.Contains(Path.GetExtension(filePath));
}
