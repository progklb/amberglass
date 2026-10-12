using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Video;

/// <summary>
/// Reads duration, dimensions and frame rate with ffprobe.
///
/// ExifTool already surfaces most of this for the inspector, but the player needs values it can do
/// arithmetic with — a timeline cannot be laid out from the string "0:00:12".
/// </summary>
public sealed class FfprobeVideoInfoProvider : IVideoInfoProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfprobeVideoInfoProvider> _logger;

    public FfprobeVideoInfoProvider(IFfmpegLocator locator, ILogger<FfprobeVideoInfoProvider> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsAvailable => _locator.FfprobePath is not null;

    public Task<VideoMediaInfo?> GetInfoAsync(MediaFile file, CancellationToken cancellationToken = default)
        => GetInfoAsync(file.FullPath, cancellationToken);

    public async Task<VideoMediaInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_locator.FfprobePath is not { } ffprobe)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in (string[])
                 [
                     "-v", "error",
                     "-show_entries", "stream=codec_type,width,height,avg_frame_rate,r_frame_rate:stream_side_data=rotation:format=duration",
                     "-of", "json",
                     path
                 ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var json = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return Parse(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed reading {File}", path);
            return null;
        }
    }

    /// <summary>
    /// Rotation in degrees from the stream's display matrix, or 0. ffprobe reports it as side data,
    /// and older files carry it as a "rotate" tag instead, so both are accepted.
    /// </summary>
    internal static double ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var degrees))
                {
                    return degrees;
                }
            }
        }

        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("rotate", out var tag) &&
            double.TryParse(tag.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagged))
        {
            return tagged;
        }

        return 0;
    }

    /// <summary>True for a quarter turn either way, which is what swaps width and height.</summary>
    internal static bool IsQuarterTurn(double degrees)
    {
        var normalised = Math.Abs(degrees % 180);
        return normalised is > 45 and < 135;
    }

    internal static VideoMediaInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array ||
                streams.GetArrayLength() == 0)
            {
                return null;
            }

            // Every stream is listed now, not just the first video one, so the video stream has to
            // be picked out - and audio can be spotted in the same pass.
            var hasAudio = false;
            JsonElement? video = null;
            JsonElement? dimensioned = null;

            foreach (var candidate in streams.EnumerateArray())
            {
                var type = candidate.TryGetProperty("codec_type", out var t) ? t.GetString() : null;

                if (string.Equals(type, "audio", StringComparison.Ordinal))
                {
                    hasAudio = true;
                    continue;
                }

                if (video is null && string.Equals(type, "video", StringComparison.Ordinal))
                {
                    video = candidate;
                }

                // Fallback for output that does not label its streams: anything with dimensions is
                // a picture of some kind, which is the only thing this needs from it.
                dimensioned ??= candidate.TryGetProperty("width", out _) ? candidate : null;
            }

            if ((video ?? dimensioned) is not { } stream)
            {
                return null;
            }
            var width = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
            var height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 0;

            // Cameras and phones record portrait footage as a landscape stream plus a rotation, and
            // ffmpeg applies that rotation when decoding. The stored dimensions are therefore not
            // the ones frames arrive in, and scaling to them squashes the picture — so report what
            // will actually come out.
            if (IsQuarterTurn(ReadRotation(stream)))
            {
                (width, height) = (height, width);
            }

            var frameRate = ParseRational(stream, "avg_frame_rate");
            if (frameRate <= 0)
            {
                frameRate = ParseRational(stream, "r_frame_rate");
            }

            var duration = TimeSpan.Zero;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return new VideoMediaInfo(duration, width, height, frameRate, hasAudio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ffprobe reports frame rates as "30000/1001" rather than a decimal.</summary>
    private static double ParseRational(JsonElement stream, string property)
    {
        if (!stream.TryGetProperty(property, out var value) || value.GetString() is not { } text)
        {
            return 0;
        }

        var parts = text.Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
        {
            return 0;
        }

        return numerator / denominator;
    }
}
