using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Images;

/// <summary>
/// Decodes still images Skia understands directly. RAW formats are handled by
/// <see cref="RawThumbnailGenerator"/>, which extracts their embedded preview first.
/// </summary>
public sealed class SkiaImageThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<SkiaImageThumbnailGenerator> _logger;

    public SkiaImageThumbnailGenerator(ILogger<SkiaImageThumbnailGenerator> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(MediaFile file)
        => file.MediaType == MediaType.Image && SkiaThumbnailRenderer.CanDecode(file.FullPath);

    public Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(file, maxEdgePixels, cancellationToken), cancellationToken);

    private byte[]? Generate(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(file.FullPath);
            return SkiaThumbnailRenderer.Render(stream, maxEdgePixels, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Routine rather than exceptional: the catalog outlives the files it indexes, so a
            // search can legitimately return something that has since been moved or deleted. A
            // stack trace here reads like a fault and buries the real ones.
            _logger.LogDebug("Skipping a thumbnail for {File}, which no longer exists", file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate thumbnail for {File}", file.FullPath);
            return null;
        }
    }
}
