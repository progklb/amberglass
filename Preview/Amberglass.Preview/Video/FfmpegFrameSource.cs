using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Video;

/// <summary>
/// Decodes video into raw BGRA frames for on-screen playback.
///
/// One long-lived ffmpeg process writes uncompressed frames to stdout and this reads them at a
/// fixed size, which is far cheaper than spawning a process per frame. Buffers come from
/// <see cref="ArrayPool{T}"/> because a 720p frame is 3.5 MB and playing 25 of them a second would
/// otherwise produce roughly 90 MB/s of garbage.
/// </summary>
public sealed class FfmpegFrameSource : IVideoFrameSource
{
    private static readonly TimeSpan SingleFrameTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Decode is capped at 720p regardless of source: this feeds a preview pane, and decoding a 5.3K
    /// frame to shrink it on screen is exactly the waste proxies exist to avoid.
    /// </summary>
    private const int MaxDecodeHeight = 720;

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegFrameSource> _logger;

    public FfmpegFrameSource(IFfmpegLocator locator, ILogger<FfmpegFrameSource> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public async IAsyncEnumerable<VideoFrame> StreamAsync(
        string path,
        VideoMediaInfo info,
        TimeSpan position,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg || !info.IsUsable)
        {
            yield break;
        }

        var (width, height) = DecodeSize(info);
        var frameBytes = width * height * 4;

        var startInfo = BuildStartInfo(ffmpeg, path, position, width, height, singleFrame: false);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            yield break;
        }

        // Drained so a full stderr pipe cannot stall the decoder mid-playback.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            var stream = process.StandardOutput.BaseStream;
            var frameDuration = info.FrameDuration;
            var index = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);

                var read = await ReadExactlyAsync(stream, buffer, frameBytes, cancellationToken).ConfigureAwait(false);
                if (!read)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    yield break;
                }

                var timestamp = position + (frameDuration * index++);
                yield return new VideoFrame(buffer, frameBytes, width, height, timestamp, ReturnBuffer);
            }
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    public async Task<VideoFrame?> GetFrameAsync(
        string path,
        VideoMediaInfo info,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg || !info.IsUsable)
        {
            return null;
        }

        var (width, height) = DecodeSize(info);
        var frameBytes = width * height * 4;

        var startInfo = BuildStartInfo(ffmpeg, path, position, width, height, singleFrame: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SingleFrameTimeout);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);

        try
        {
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var read = await ReadExactlyAsync(process.StandardOutput.BaseStream, buffer, frameBytes, timeout.Token)
                .ConfigureAwait(false);

            if (!read)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }

            return new VideoFrame(buffer, frameBytes, width, height, position, ReturnBuffer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        catch (Exception ex)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.LogDebug(ex, "Could not decode a frame of {Path} at {Position}", path, position);
            return null;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string ffmpeg,
        string path,
        TimeSpan position,
        int width,
        int height,
        bool singleFrame)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Add(string value) => startInfo.ArgumentList.Add(value);

        Add("-hide_banner");
        Add("-loglevel");
        Add("error");
        Add("-nostdin");

        // Seeking before -i uses the container index, which is far faster than decoding forward to
        // the target. Accuracy is good enough for a preview and unnoticeable while scrubbing.
        if (position > TimeSpan.Zero)
        {
            Add("-ss");
            Add(position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        Add("-i");
        Add(path);

        if (singleFrame)
        {
            Add("-frames:v");
            Add("1");
        }

        Add("-vf");
        Add($"scale={width}:{height}");
        Add("-f");
        Add("rawvideo");
        Add("-pix_fmt");
        Add("bgra");
        Add("-an");
        Add("pipe:1");

        return startInfo;
    }

    /// <summary>ArrayPool's Return has optional parameters, so it needs wrapping to match Action&lt;byte[]&gt;.</summary>
    private static readonly Action<byte[]> ReturnBuffer = buffer => ArrayPool<byte>.Shared.Return(buffer);

    /// <summary>Even dimensions, capped height, aspect preserved.</summary>
    internal static (int Width, int Height) DecodeSize(VideoMediaInfo info)
    {
        var height = Math.Min(info.Height, MaxDecodeHeight);
        var scale = height / (double)info.Height;
        var width = (int)Math.Round(info.Width * scale);

        return (Math.Max(2, width - (width % 2)), Math.Max(2, height - (height % 2)));
    }

    /// <summary>
    /// A pipe delivers whatever happens to be available, so a frame usually arrives in several
    /// reads. Returns false at end of stream.
    /// </summary>
    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
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
            _logger.LogDebug(ex, "Unable to terminate the ffmpeg decode process");
        }
    }
}
