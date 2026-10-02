using System.Diagnostics;
using System.Text.Json;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Metadata.ExifTool;

/// <summary>
/// Extracts the embedded preview with a one-shot ExifTool process.
///
/// This deliberately does not use the shared <c>-stay_open</c> session: that session reads stdout as
/// <b>text</b>, line by line, looking for the <c>{ready}</c> marker. Pushing JPEG bytes through it
/// would corrupt them. A separate process gives a clean binary stdout, and the cost is paid once per
/// file because the result is cached as a thumbnail.
///
/// The file is only ever read.
/// </summary>
public sealed class ExifToolPreviewExtractor : IEmbeddedPreviewExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Preview tags in descending order of usefulness. <c>PreviewImage</c> is typically a
    /// full-resolution JPEG; <c>ThumbnailImage</c> is a last resort at around 160px.
    /// </summary>
    private static readonly string[] PreviewTags =
    [
        "-PreviewImage",
        "-JpgFromRaw",
        "-OtherImage",
        "-ThumbnailImage"
    ];

    private readonly IExifToolLocator _locator;
    private readonly ExifToolHost _host;
    private readonly ILogger<ExifToolPreviewExtractor> _logger;

    public ExifToolPreviewExtractor(
        IExifToolLocator locator,
        ExifToolHost host,
        ILogger<ExifToolPreviewExtractor> logger)
    {
        _locator = locator;
        _host = host;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public async Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        if (_locator.ExifToolPath is not { } exifTool)
        {
            return null;
        }

        foreach (var tag in PreviewTags)
        {
            var preview = await TryExtractAsync(exifTool, tag, file, cancellationToken).ConfigureAwait(false);
            if (preview is not null)
            {
                // Only once a preview is in hand, so a file with nothing to show costs nothing extra.
                return await WithOrientationAsync(preview, file, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("No embedded preview found in {File}", file.FullPath);
        return null;
    }

    /// <summary>
    /// Attaches the RAW's own orientation to the extracted bytes.
    ///
    /// Asked of the shared <c>-stay_open</c> session rather than a new process: these are text tags,
    /// which is all that session can carry, and a thumbnail should not cost two process starts. When
    /// the session is unavailable the preview is still returned — a picture the right way up is worth
    /// more than no picture, and this is the behaviour that predates the orientation being read.
    /// </summary>
    private async Task<EmbeddedPreview> WithOrientationAsync(
        byte[] preview,
        MediaFile file,
        CancellationToken cancellationToken)
    {
        if (_host.Session is not { } session)
        {
            return new EmbeddedPreview(preview);
        }

        try
        {
            // -n for the orientation as its number rather than ExifTool's wording. The stored size
            // comes along because it is what tells us whether the preview has already been rotated.
            var json = await session.ExecuteAsync(
                ["-json", "-n", "-EXIF:Orientation", "-ImageWidth", "-ImageHeight", file.FullPath],
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return new EmbeddedPreview(preview);
            }

            var entry = document.RootElement[0];

            return new EmbeddedPreview(
                preview,
                Number(entry, "Orientation"),
                Number(entry, "ImageWidth"),
                Number(entry, "ImageHeight"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the orientation of {File}", file.FullPath);
            return new EmbeddedPreview(preview);
        }
    }

    private static int? Number(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private async Task<byte[]?> TryExtractAsync(
        string exifTool,
        string tag,
        MediaFile file,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifTool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add(tag);
        startInfo.ArgumentList.Add(file.FullPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractionTimeout);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (buffer.Length == 0)
            {
                return null;
            }

            var bytes = buffer.ToArray();
            return LooksLikeJpeg(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ExifTool timed out extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExifTool failed extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    /// <summary>
    /// Guards against ExifTool writing a diagnostic to stdout instead of image data — decoding
    /// that would fail later and much less clearly.
    /// </summary>
    private static bool LooksLikeJpeg(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogDebug(ex, "Unable to terminate the ExifTool process");
        }
    }
}
