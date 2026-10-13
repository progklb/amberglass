using System.Security.Cryptography;
using System.Text;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;

namespace Amberglass.Preview.Cache;

/// <summary>
/// Content-addressed thumbnail store on disk. The cache key includes the source file's size and
/// modification time, so a file edited externally naturally misses the cache instead of serving a
/// stale image.
/// </summary>
public sealed class ThumbnailCache
{
    private readonly IAppPaths _paths;

    public ThumbnailCache(IAppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Bumped when a change to rendering makes previously cached thumbnails wrong, so they are
    /// regenerated rather than served for the life of the file.
    ///
    /// Only applied to the files a change could have affected — see <see cref="IsVersioned"/>. A
    /// library of JPEGs should not pay for a fix to RAW handling.
    ///
    /// 2: a RAW's embedded preview is now turned to match the orientation recorded in the RAW, which
    /// previously left frames shot in portrait cached on their side.
    /// </summary>
    private const int RawRendererVersion = 2;

    /// <summary>
    /// Bumped for changes that only touch DNGs, so a library of other RAWs is not regenerated for them.
    ///
    /// 1: a stitched panorama's transparency mask is applied, turning its dead space black rather
    /// than the white its preview was painted with.
    ///
    /// 2: the largest rendered preview inside the DNG is used, so tiles that were developed by
    /// ImageIO for want of a large enough preview are now Lightroom's own rendering, edits included.
    /// </summary>
    private const int DngRendererVersion = 2;

    public string GetCacheKey(MediaFile file, int maxEdgePixels)
    {
        var version = IsVersioned(file) ? $"|v{RawRendererVersion}" : string.Empty;
        if (file.FullPath.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
        {
            version += $"|dng{DngRendererVersion}";
        }

        var raw = $"{file.FullPath}|{file.SizeBytes}|{file.ModifiedUtc.UtcTicks}|{maxEdgePixels}{version}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Whether this file's thumbnail comes from the RAW path, and so is subject to
    /// <see cref="RawRendererVersion"/>. The same test the RAW generator uses to claim a file, kept
    /// as an extension check so it costs nothing and needs no file open.
    /// </summary>
    private static bool IsVersioned(MediaFile file)
        => file.MediaType == MediaType.Image && !SkiaThumbnailRenderer.CanDecode(file.FullPath);

    public string GetCachePath(string key)
    {
        // Shard by the first two hex characters to keep directory sizes manageable on large libraries.
        var folder = Path.Combine(_paths.ThumbnailCacheRoot, key[..2]);
        return Path.Combine(folder, key + ".jpg");
    }

    public async Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = GetCachePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        var path = GetCachePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file then move, so a cancelled or crashed write never leaves a truncated
        // thumbnail that would be served as a cache hit later.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temp);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
