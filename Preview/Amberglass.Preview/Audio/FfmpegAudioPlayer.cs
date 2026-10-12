using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Audio;

/// <summary>
/// Plays a file's audio track by decoding it with ffmpeg and feeding the samples to the sound
/// device.
///
/// The same ffmpeg that already produces video frames produces audio here, from the same file and
/// the same position, so the two stay together without a second decoder library. ffmpeg resamples
/// whatever the source is to one fixed PCM format, so the output device never has to negotiate.
/// </summary>
public sealed class FfmpegAudioPlayer : IAudioPlayer
{
    /// <summary>
    /// Read size. Small enough that a volume change is heard almost immediately — volume is applied
    /// as samples pass through, so it only takes effect on audio not yet handed to the device.
    /// </summary>
    private const int ChunkBytes = 8192;

    private readonly IFfmpegLocator _locator;
    private readonly IAudioOutput _output;
    private readonly ILogger<FfmpegAudioPlayer> _logger;

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private volatile int _volumeScale = ScaleOne;

    /// <summary>
    /// Volume as a fixed-point multiplier. Integer so the audio path does no floating-point work
    /// per sample, and so reads of it are atomic without a lock.
    /// </summary>
    private const int ScaleOne = 1 << 12;

    public FfmpegAudioPlayer(IFfmpegLocator locator, IAudioOutput output, ILogger<FfmpegAudioPlayer> logger)
    {
        _locator = locator;
        _output = output;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable && _output.IsAvailable;

    public double Volume
    {
        get => (double)_volumeScale / ScaleOne;
        set => _volumeScale = (int)(Math.Clamp(value, 0, 1) * ScaleOne);
    }

    public async Task StartAsync(string path, TimeSpan from, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);

        if (!IsAvailable)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = cts;

        _output.Start(AudioFormat.Default);
        _pump = Task.Run(() => PumpAsync(path, from, cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is not { } cts)
        {
            return;
        }

        _cts = null;

        await cts.CancelAsync().ConfigureAwait(false);

        // Stopping the device first unblocks the pump if it is waiting for queue space.
        _output.Stop();

        if (_pump is { } pump)
        {
            _pump = null;

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _output.Dispose();
    }

    private async Task PumpAsync(string path, TimeSpan from, CancellationToken cancellationToken)
    {
        if (_locator.FfmpegPath is not { } ffmpeg)
        {
            return;
        }

        using var process = Process.Start(BuildStartInfo(ffmpeg, path, from));
        if (process is null)
        {
            return;
        }

        // Drained so a full stderr pipe cannot stall the decoder.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        try
        {
            var stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkBytes), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                ApplyVolume(buffer.AsSpan(0, read));

                // Blocks once the device is a few buffers ahead, which is what holds decoding to
                // realtime rather than letting ffmpeg run to the end of the file.
                _output.Write(buffer.AsSpan(0, read), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio playback failed for {Path}", path);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            KillIfRunning(process);
        }
    }

    /// <summary>
    /// Scales 16-bit samples in place. Done here rather than through a device volume control so it
    /// works identically on any output, and so muting is exact rather than merely quiet.
    /// </summary>
    private void ApplyVolume(Span<byte> pcm)
    {
        var scale = _volumeScale;
        if (scale == ScaleOne)
        {
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(samples[i] * scale / ScaleOne);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string ffmpeg, string path, TimeSpan from)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var format = AudioFormat.Default;

        foreach (var argument in (string[])
                 [
                     "-hide_banner",
                     "-loglevel", "error",
                     "-nostdin",

                     // Before -i, so ffmpeg seeks rather than decoding and discarding everything
                     // up to the start position.
                     "-ss", from.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", path,

                     // Video is decoded separately; asking for it here would be wasted work.
                     "-vn",
                     "-f", "s16le",
                     "-acodec", "pcm_s16le",
                     "-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture),
                     "-ac", format.Channels.ToString(CultureInfo.InvariantCulture),
                     "-"
                 ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the audio decoder");
        }
    }
}
