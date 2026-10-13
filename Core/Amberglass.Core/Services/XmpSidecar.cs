namespace Amberglass.Core.Services;

/// <summary>
/// Locates the XMP sidecar for a media file.
///
/// Two conventions are in the wild and both must be recognised:
///   IMG001.CR3 → IMG001.xmp      (Adobe's convention — replaces the extension)
///   IMG001.CR3 → IMG001.CR3.xmp  (used by some tools — appends to the full name)
/// The Adobe form is preferred when creating new sidecars, but either is read.
/// </summary>
public static class XmpSidecar
{
    /// <summary>The path this application uses when it creates a sidecar.</summary>
    public static string GetPreferredPath(string mediaPath)
        => Path.ChangeExtension(mediaPath, ".xmp");

    /// <summary>
    /// Whether one of <paramref name="mediaFileName"/>'s sidecars is among the names in its folder.
    /// Case is ignored, so <c>IMG001.XMP</c> counts on a case-sensitive filesystem as it does on
    /// the others — the set should be built with an ignore-case comparer.
    /// </summary>
    public static bool IsAmong(string mediaFileName, IReadOnlySet<string> namesInFolder)
        => GetCandidatePaths(mediaFileName).Any(namesInFolder.Contains);

    /// <summary>Whether a file name is a sidecar's, whatever its case.</summary>
    public static bool IsSidecarName(string fileName)
        => fileName.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Both recognised sidecar locations, most-preferred first.</summary>
    public static IEnumerable<string> GetCandidatePaths(string mediaPath)
    {
        yield return GetPreferredPath(mediaPath);
        yield return mediaPath + ".xmp";
    }

    /// <summary>
    /// When the media file's sidecar was last written, as seconds since the epoch, or 0 when there
    /// is no sidecar.
    ///
    /// Part of deciding whether the catalog is stale. A rating or label written to a sidecar — by
    /// this application, or by Lightroom or Bridge — does not touch the media file at all, so its
    /// size and modified time say nothing has changed while the metadata has changed completely.
    /// 0 for "none" so that gaining a sidecar and losing one both read as a difference.
    /// </summary>
    public static long LastWrittenUtc(string mediaPath)
    {
        if (Find(mediaPath) is not { } sidecar)
        {
            return 0;
        }

        try
        {
            return new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(sidecar)).ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not unchanged: 0 differs from any real stamp, so the file is re-read
            // rather than assumed current.
            return 0;
        }
    }

    /// <summary>The existing sidecar for a media file, or null when there is none.</summary>
    public static string? Find(string mediaPath)
    {
        foreach (var candidate in GetCandidatePaths(mediaPath))
        {
            // A file whose own extension is .xmp would otherwise "find itself".
            if (string.Equals(candidate, mediaPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
