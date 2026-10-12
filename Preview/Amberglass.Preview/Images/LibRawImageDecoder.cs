using System.Diagnostics;
using System.Globalization;
using System.Text;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Images;

/// <summary>
/// Develops RAW files with LibRaw's <c>dcraw_emu</c>.
///
/// The CLI rather than the library: it is how ExifTool and FFmpeg are already used, it keeps LibRaw
/// at arm's length behind a process boundary — a malformed RAW crashes the tool, not the
/// application — and it avoids shipping and signing a native binary.
///
/// Output comes back as a PPM on stdout rather than through a temporary file. A 26MP develop is
/// 78 MB, and writing that to disk and reading it back on every image would cost more than the
/// demosaic.
/// </summary>
public sealed class LibRawImageDecoder : IRawDecoder
{
    private readonly ILibRawLocator _locator;
    private readonly ISettingsService _settings;
    private readonly ILogger<LibRawImageDecoder> _logger;

    public LibRawImageDecoder(ILibRawLocator locator, ISettingsService settings, ILogger<LibRawImageDecoder> logger)
    {
        _locator = locator;
        _settings = settings;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public async Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        if (_locator.DcrawPath is not { } dcraw || file.MediaType != MediaType.Image)
        {
            return null;
        }

        Process? process = null;

        try
        {
            process = Process.Start(BuildStartInfo(dcraw, file.FullPath, _settings.Current.RawDevelop));
            if (process is null)
            {
                return null;
            }

            // Drained so a full stderr pipe cannot stall the develop.
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || buffer.Length == 0)
            {
                _logger.LogWarning(
                    "LibRaw could not develop {File}: {Error}",
                    file.FullPath,
                    (await errors.ConfigureAwait(false)).Trim());

                return null;
            }

            return ParsePpm(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) is { } image
                ? image with { Renderer = DecodedImage.LibRaw }
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to develop {File}", file.FullPath);
            return null;
        }
        finally
        {
            // Disposing a Process does not stop it. Without this, moving through a folder of RAWs
            // leaves an abandoned develop running for every one passed over, each burning a core.
            KillIfRunning(process);
            process?.Dispose();
        }
    }

    private void KillIfRunning(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the RAW develop");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string dcraw, string path, RawDevelopSettings develop)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dcraw,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in BuildArguments(path, develop))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Translates the develop settings into dcraw_emu arguments. Kept apart from launching anything
    /// so the mapping can be tested — a wrong flag here is a silently different photograph.
    /// </summary>
    internal static List<string> BuildArguments(string path, RawDevelopSettings develop)
    {
        var arguments = new List<string>();

        // White balance. Camera is the picture as shot; auto is LibRaw averaging the frame.
        arguments.Add(develop.WhiteBalance == RawWhiteBalance.Auto ? "-a" : "-w");

        // Highlight handling: the main reason for opening the RAW rather than the JPEG.
        arguments.Add("-H");
        arguments.Add(((int)develop.Highlights).ToString(CultureInfo.InvariantCulture));

        // sRGB, matching what the display expects. Wider spaces are deliberately not offered: with
        // no colour management they would be shown as though they were sRGB, which misrepresents
        // the colour rather than improving it.
        arguments.Add("-o");
        arguments.Add("1");

        // Interpolation. The difference between culling a folder and judging a frame.
        arguments.Add("-q");
        arguments.Add(develop.InterpolationCode.ToString(CultureInfo.InvariantCulture));

        if (develop.NoiseReduction != RawNoiseReduction.Off)
        {
            arguments.Add("-fbdd");
            arguments.Add(((int)develop.NoiseReduction).ToString(CultureInfo.InvariantCulture));
        }

        // Only when asked for: passing an exposure shift of 1.0 is not quite a no-op in LibRaw, and
        // "as shot" should mean exactly that.
        if (Math.Abs(develop.ExposureStops) > 0.001)
        {
            arguments.Add("-aexpo");
            arguments.Add(develop.ExposureMultiplier.ToString("0.###", CultureInfo.InvariantCulture));

            // Highlight preservation, on the assumption that someone lifting exposure does not want
            // the sky blown to do it.
            arguments.Add("1");
        }

        // Straight to stdout. A 26MP develop is 78MB, and writing that to a temporary file and
        // reading it back on every image would cost more than the demosaic. 8 bits a channel is the
        // default, which is what a screen can show anyway.
        arguments.Add("-Z");
        arguments.Add("-");

        arguments.Add(path);

        return arguments;
    }

    /// <summary>
    /// Parses binary PPM (P6) into BGRA. Written out rather than pulled from a library because the
    /// format is a header and a block of RGB bytes, and this is the hot path for a 26MP image.
    /// </summary>
    internal static DecodedImage? ParsePpm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != (byte)'P' || data[1] != (byte)'6')
        {
            return null;
        }

        var offset = 2;

        if (!TryReadHeaderValue(data, ref offset, out var width) ||
            !TryReadHeaderValue(data, ref offset, out var height) ||
            !TryReadHeaderValue(data, ref offset, out var maxValue))
        {
            return null;
        }

        // A single whitespace byte separates the header from the pixels.
        offset++;

        // 16-bit PPM would need different unpacking; nothing here asks for it.
        if (width <= 0 || height <= 0 || maxValue != 255)
        {
            return null;
        }

        var pixels = (long)width * height;
        if (offset + (pixels * 3) > data.Length)
        {
            return null;
        }

        var bgra = new byte[pixels * 4];
        var source = data[offset..];

        for (long i = 0; i < pixels; i++)
        {
            var from = (int)(i * 3);
            var to = (int)(i * 4);

            // PPM is RGB; the UI blits BGRA.
            bgra[to] = source[from + 2];
            bgra[to + 1] = source[from + 1];
            bgra[to + 2] = source[from];
            bgra[to + 3] = 255;
        }

        return new DecodedImage(bgra, width, height);
    }

    /// <summary>
    /// Reads one whitespace-delimited number, skipping comments. PPM allows both between any two
    /// header fields, and dcraw's own output is not the only thing this might ever see.
    /// </summary>
    private static bool TryReadHeaderValue(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;

        while (offset < data.Length)
        {
            var c = data[offset];

            if (c == (byte)'#')
            {
                while (offset < data.Length && data[offset] != (byte)'\n')
                {
                    offset++;
                }

                continue;
            }

            if (!char.IsWhiteSpace((char)c))
            {
                break;
            }

            offset++;
        }

        var start = offset;

        while (offset < data.Length && data[offset] >= (byte)'0' && data[offset] <= (byte)'9')
        {
            offset++;
        }

        return offset > start
               && int.TryParse(Encoding.ASCII.GetString(data[start..offset]), out value);
    }
}
