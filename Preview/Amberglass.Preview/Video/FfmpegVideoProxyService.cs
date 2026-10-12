using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Video;

/// <summary>
/// Generates and caches low-resolution stand-ins so browsing never decodes a 5.3K source.
///
/// Proxies keep their audio even though nothing plays it yet: encoding is the expensive step, and
/// producing silent proxies now would mean regenerating every one of them later.
/// </summary>
public sealed class FfmpegVideoProxyService : IVideoProxyService
{
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(30);

    private readonly IFfmpegLocator _locator;
    private readonly IVideoInfoProvider _info;
    private readonly IAppPaths _paths;
    private readonly ICacheMaintenance? _maintenance;
    private readonly ILogger<FfmpegVideoProxyService> _logger;

    /// <summary>
    /// Encoding is heavy, so one at a time. Two users of the same file also share a single job
    /// rather than racing to write the same output.
    /// </summary>
    private readonly SemaphoreSlim _encodeGate = new(1, 1);
    private readonly Dictionary<string, Task<VideoProxy?>> _inFlight = [];

    public FfmpegVideoProxyService(
        IFfmpegLocator locator,
        IVideoInfoProvider info,
        IAppPaths paths,
        ILogger<FfmpegVideoProxyService> logger,
        ICacheMaintenance? maintenance = null)
    {
        _locator = locator;
        _info = info;
        _paths = paths;
        _logger = logger;
        _maintenance = maintenance;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public bool HasProxy(MediaFile file, VideoQuality quality)
        => quality == VideoQuality.Original || File.Exists(GetProxyPath(file, quality));

    public async Task<VideoProxy?> GetProxyAsync(
        MediaFile file,
        VideoQuality quality,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await _info.GetInfoAsync(file, cancellationToken).ConfigureAwait(false);
        if (info is null || !info.IsUsable)
        {
            return null;
        }

        // Original plays straight from the source; there is nothing to generate or cache.
        if (quality == VideoQuality.Original)
        {
            return new VideoProxy(file.FullPath, quality, file.FullPath, info);
        }

        // A source already smaller than the requested proxy would only be upscaled — pointless work
        // and a bigger file than the original.
        if (info.Height <= (int)quality)
        {
            return new VideoProxy(file.FullPath, VideoQuality.Original, file.FullPath, info);
        }

        var proxyPath = GetProxyPath(file, quality);
        if (File.Exists(proxyPath))
        {
            progress?.Report(1);
            return new VideoProxy(file.FullPath, quality, proxyPath, await DescribeAsync(proxyPath, info, cancellationToken).ConfigureAwait(false));
        }

        Task<VideoProxy?> job;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(proxyPath, out var existing))
            {
                existing = GenerateAsync(file, quality, info, proxyPath, progress, cancellationToken);
                _inFlight[proxyPath] = existing;
            }

            job = existing;
        }

        try
        {
            return await job.ConfigureAwait(false);
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(proxyPath);
            }
        }
    }

    private async Task<VideoProxy?> GenerateAsync(
        MediaFile file,
        VideoQuality quality,
        VideoMediaInfo info,
        string proxyPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (_locator.FfmpegPath is not { } ffmpeg)
        {
            return null;
        }

        await _encodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(proxyPath)!);

            // Encode to a temp name and move on success, so an interrupted or failed encode never
            // leaves a truncated proxy that would later be treated as a valid cache hit.
            var temporary = proxyPath + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";

            try
            {
                var succeeded = await RunFfmpegAsync(ffmpeg, file.FullPath, temporary, quality, info, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!succeeded || !File.Exists(temporary))
                {
                    return null;
                }

                File.Move(temporary, proxyPath, overwrite: true);

                var written = new FileInfo(proxyPath).Length;
                _maintenance?.NotifyBytesWritten(written);

                _logger.LogInformation(
                    "Generated a {Quality} proxy for {File} ({Size})",
                    quality, file.FileName, ByteSize.Format(written));

                return new VideoProxy(file.FullPath, quality, proxyPath,
                    await DescribeAsync(proxyPath, info, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed generating a {Quality} proxy for {File}", quality, file.FullPath);
            return null;
        }
        finally
        {
            _encodeGate.Release();
        }
    }

    private async Task<bool> RunFfmpegAsync(
        string ffmpeg,
        string sourcePath,
        string outputPath,
        VideoQuality quality,
        VideoMediaInfo info,
        IProgress<double>? progress,
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

        foreach (var argument in BuildArguments(sourcePath, outputPath, quality))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GenerationTimeout);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await ReadProgressAsync(process, info, progress, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                progress?.Report(1);
                return true;
            }

            _logger.LogWarning("ffmpeg failed encoding {Source}: {Error}", sourcePath, await errorTask.ConfigureAwait(false));
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    private static IEnumerable<string> BuildArguments(string sourcePath, string outputPath, VideoQuality quality)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";
        yield return "-i";
        yield return sourcePath;

        // Even height keeps H.264 happy; -2 lets ffmpeg pick the width that preserves aspect.
        yield return "-vf";
        yield return $"scale=-2:{(int)quality}";

        // VideoToolbox on Apple Silicon encodes far faster than libx264 and leaves the CPU free for
        // browsing. It is the platform's hardware encoder, so quality at these bitrates is ample.
        yield return "-c:v";
        yield return OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";

        if (!OperatingSystem.IsMacOS())
        {
            yield return "-preset";
            yield return "veryfast";
            yield return "-crf";
            yield return "23";
        }
        else
        {
            yield return "-b:v";
            yield return BitrateFor(quality);
        }

        yield return "-c:a";
        yield return "aac";
        yield return "-b:a";
        yield return "128k";

        // Seeking is the whole point of a proxy, so put the index at the front.
        yield return "-movflags";
        yield return "+faststart";

        yield return "-progress";
        yield return "pipe:1";

        yield return "-y";
        yield return outputPath;
    }

    private static string BitrateFor(VideoQuality quality) => quality switch
    {
        VideoQuality.P720 => "4M",
        VideoQuality.P480 => "2M",
        _ => "1M"
    };

    /// <summary>
    /// ffmpeg's <c>-progress</c> stream reports <c>out_time_ms</c>, which against the known duration
    /// gives a real percentage rather than a spinner.
    /// </summary>
    private static async Task ReadProgressAsync(
        Process process,
        VideoMediaInfo info,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null || info.Duration <= TimeSpan.Zero)
        {
            await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var totalMicroseconds = info.Duration.TotalSeconds * 1_000_000;

        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                continue;
            }

            if (long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
            {
                progress.Report(Math.Clamp(microseconds / totalMicroseconds, 0, 1));
            }
        }
    }

    private async Task<VideoMediaInfo> DescribeAsync(string proxyPath, VideoMediaInfo fallback, CancellationToken cancellationToken)
    {
        if (_info is FfprobeVideoInfoProvider probe)
        {
            var described = await probe.GetInfoAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            if (described is { IsUsable: true })
            {
                return described;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Keyed like the thumbnail cache — path, size and modification time — so re-encoding a source
    /// naturally produces a different proxy rather than serving a stale one.
    /// </summary>
    internal string GetProxyPath(MediaFile file, VideoQuality quality)
    {
        var raw = $"{file.FullPath}|{file.SizeBytes}|{file.ModifiedUtc.UtcTicks}|{(int)quality}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        return Path.Combine(_paths.VideoProxyCacheRoot, key[..2], $"{key}.mp4");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove the temporary proxy {Path}", path);
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
            _logger.LogDebug(ex, "Unable to terminate the ffmpeg process");
        }
    }
}
