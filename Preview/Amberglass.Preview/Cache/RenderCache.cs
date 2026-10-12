using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;

namespace Amberglass.Preview.Cache;

/// <summary>
/// Full-resolution renditions of developed RAW files, on disk.
///
/// Developing a RAW costs three to four seconds every single time, and until now that was paid again
/// on every visit to the same photograph. This makes the second visit a decode instead — well under a
/// second — which is the difference between a browsable folder of RAWs and one you avoid.
///
/// Only RAW files are worth storing. Re-encoding a camera JPEG would spend twenty megabytes to save
/// about four hundred milliseconds against simply decoding the original, and the original is already
/// on the disk.
/// </summary>
public sealed class RenderCache
{
    /// <summary>
    /// Bumped when the stored form changes in a way older entries cannot satisfy. Cheaper and safer
    /// than migrating a cache whose entries are all reproducible from the originals.
    /// </summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// JPEG rather than something lossless, at a quality where the artefacts sit below the noise of
    /// a developed sensor image.
    ///
    /// The alternative is 100 MB an entry — a 26MP frame is 104 MB of BGRA, and PNG barely dents
    /// photographic data while costing seconds to encode, which would eat the very time this exists
    /// to save. At this quality the same frame stores in about 6.5 MB, with chroma left unsubsampled
    /// so colour detail survives being looked at 1:1. This is a cache of a rendering, not an archive:
    /// the photograph is untouched on disk and can always be developed again.
    /// </summary>
    public const int Quality = 95;

    /// <summary>
    /// Which decoder produced an entry, carried in the file name.
    ///
    /// It has to survive the round trip: only a LibRaw develop answers to the develop settings, and
    /// the viewer says so. Serving a cache hit without knowing would silently drop that warning. One
    /// letter rather than a sidecar file, so a lookup stays a handful of exact path probes.
    /// </summary>
    private static readonly (string Code, string Renderer)[] Renderers =
    [
        ("L", DecodedImage.LibRaw),
        ("P", DecodedImage.Platform)
    ];

    private readonly IAppPaths _paths;

    public RenderCache(IAppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// True for files this cache is worth using at all. Everything else decodes fast enough from the
    /// original that storing a copy would be a waste of disk.
    /// </summary>
    public static bool IsWorthCaching(MediaFile file, bool developRawFiles)
        => developRawFiles && MediaTypeRegistry.IsRaw(file.FullPath);

    /// <summary>
    /// Identifies a rendition by its source file <b>and</b> by how it was developed.
    ///
    /// The develop settings are part of the identity, not incidental to it: change the exposure or
    /// the highlight handling and every stored rendition describes a picture the application would no
    /// longer produce. Including them means a changed setting misses the cache rather than serving
    /// something stale, and switching back finds the earlier renditions still there.
    /// </summary>
    public string GetCacheKey(MediaFile file, RawDevelopSettings develop)
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{file.FullPath}|{file.SizeBytes}|{file.ModifiedUtc.UtcTicks}|v{FormatVersion}|{develop}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>Shard by the first two hex characters, as the thumbnail cache does.</summary>
    private string GetCachePath(string key, string code)
        => Path.Combine(_paths.RenderCacheRoot, key[..2], $"{key}-{code}.jpg");

    /// <summary>
    /// The stored rendition and the renderer that produced it, or null for a miss. Returns encoded
    /// bytes rather than pixels so the caller decides when to spend memory on decoding them.
    /// </summary>
    public async Task<(byte[] Data, string Renderer)?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        foreach (var (code, renderer) in Renderers)
        {
            var path = GetCachePath(key, code);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

                // Touched so the least-recently-used ordering reflects use rather than creation;
                // without this a rendition opened daily would be evicted before one never looked at
                // again.
                TryTouch(path);

                return (data, renderer);
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    public async Task WriteAsync(string key, string? renderer, byte[] data, CancellationToken cancellationToken)
    {
        var code = Renderers.FirstOrDefault(r => r.Renderer == renderer).Code;
        if (code is null)
        {
            // An unrecognised renderer would be read back as the wrong one, and the develop panel
            // would then lie about whether its controls apply. Better not to store it.
            return;
        }

        var path = GetCachePath(key, code);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written aside then moved, so a cancelled write cannot leave a truncated file to be served
        // as a hit later.
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

    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
