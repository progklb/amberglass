using System.Diagnostics;
using System.Globalization;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Video;

/// <summary>
/// Extracts a representative frame with FFmpeg. Reads media only; never writes to the source file.
/// </summary>
public sealed class FfmpegVideoThumbnailGenerator : IThumbnailGenerator
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegVideoThumbnailGenerator> _logger;

    public FfmpegVideoThumbnailGenerator(IFfmpegLocator locator, ILogger<FfmpegVideoThumbnailGenerator> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool CanHandle(MediaFile file) => file.MediaType == MediaType.Video && _locator.IsAvailable;

    public async Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg)
        {
            return null;
        }

        // Seeking a few seconds in avoids the black or fade-in frames that many clips open with.
        // If the clip is shorter than the seek point FFmpeg produces nothing, so fall back to frame 0.
        var frame = await TryExtractAsync(ffmpeg, file, maxEdgePixels, seekSeconds: 3, cancellationToken).ConfigureAwait(false);
        return frame ?? await TryExtractAsync(ffmpeg, file, maxEdgePixels, seekSeconds: 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryExtractAsync(
        string ffmpeg,
        MediaFile file,
        int maxEdgePixels,
        int seekSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-noaccurate_seek");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(seekSeconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(file.FullPath);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale='if(gt(iw,ih),{maxEdgePixels},-2)':'if(gt(iw,ih),-2,{maxEdgePixels})':force_original_aspect_ratio=decrease");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add("pipe:1");

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

            if (process.ExitCode != 0 || buffer.Length == 0)
            {
                var error = await errorTask.ConfigureAwait(false);
                _logger.LogDebug("FFmpeg produced no frame for {File} at {Seek}s: {Error}", file.FullPath, seekSeconds, error);
                return null;
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("FFmpeg timed out generating a thumbnail for {File}", file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFmpeg failed generating a thumbnail for {File}", file.FullPath);
            return null;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

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
            _logger.LogDebug(ex, "Unable to terminate FFmpeg process");
        }
    }
}
