using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

public class FullImageDecoderTests
{
    private sealed class NoPreviews : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable => false;

        public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<EmbeddedPreview?>(null);
    }

    /// <summary>Stands in for LibRaw, recording whether it was consulted.</summary>
    private sealed class StubRawDecoder(DecodedImage? result, bool available = true) : IRawDecoder
    {
        public int Calls { get; private set; }

        public bool IsAvailable => available;

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            Current = value;
            Changed?.Invoke(this, value);
            return Task.CompletedTask;
        }
    }

    private static SkiaFullImageDecoder Create(IRawDecoder? raw = null, bool developRaw = true)
        => new(
            new NoPreviews(),
            raw ?? new StubRawDecoder(null),
            new StubSettings(AppSettings.Default with { DevelopRawFiles = developRaw }),
            NullLogger<SkiaFullImageDecoder>.Instance);

    private static MediaFile Write(TempFolder temp, string name, int width, int height,
                                   SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }

        var path = Path.Combine(temp.Path, name);
        using (var stream = File.Create(path))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, 95))
        {
            data.SaveTo(stream);
        }

        var info = new FileInfo(path);
        return new MediaFile
        {
            FullPath = path,
            FileName = name,
            MediaType = MediaType.Image,
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = info.CreationTimeUtc
        };
    }

    [Fact]
    public async Task An_image_is_decoded_at_its_native_size()
    {
        using var temp = new TempFolder();

        // The whole point: not the 1600px the preview pipeline produces.
        var decoded = await Create().DecodeAsync(Write(temp, "big.png", 2400, 1600));

        Assert.NotNull(decoded);
        Assert.Equal(2400, decoded.Width);
        Assert.Equal(1600, decoded.Height);
    }

    [Fact]
    public async Task The_pixel_buffer_matches_the_dimensions()
    {
        using var temp = new TempFolder();

        var decoded = await Create().DecodeAsync(Write(temp, "sized.png", 320, 200));

        // Tightly packed BGRA: four bytes a pixel, no row padding.
        Assert.Equal(320 * 200 * 4, decoded!.Pixels.Length);
        Assert.Equal(320L * 200 * 4, decoded.SizeBytes);
    }

    [Fact]
    public async Task Jpeg_sources_decode_too()
    {
        using var temp = new TempFolder();

        var decoded = await Create().DecodeAsync(Write(temp, "photo.jpg", 800, 600, SKEncodedImageFormat.Jpeg));

        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.Width);
    }

    [Fact]
    public async Task Video_is_not_an_image()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "clip.png", 64, 64) with { MediaType = MediaType.Video };

        Assert.Null(await Create().DecodeAsync(file));
    }

    [Fact]
    public async Task A_missing_file_returns_null_rather_than_throwing()
    {
        var file = new MediaFile
        {
            FullPath = "/nowhere/gone.jpg",
            FileName = "gone.jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

        Assert.Null(await Create().DecodeAsync(file));
    }

    [Fact]
    public async Task A_raw_file_without_an_extractor_yields_nothing()
    {
        using var temp = new TempFolder();

        // Skia cannot decode RAW, so with no preview extractor there is nothing to show.
        var file = Write(temp, "shot.raf", 100, 100) with { FileName = "shot.raf" };

        Assert.Null(await Create().DecodeAsync(file));
    }
}

public class RawDevelopmentTests
{
    private static MediaFile Raw(string name = "shot.raf") => new()
    {
        FullPath = "/library/" + name,
        FileName = name,
        MediaType = MediaType.Image,
        SizeBytes = 27_000_000,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static List<string> Arguments(RawDevelopSettings? develop = null)
        => LibRawImageDecoder.BuildArguments("/library/shot.raf", develop ?? RawDevelopSettings.Default);

    [Fact]
    public void The_camera_white_balance_is_used_by_default()
    {
        // Without -w the develop is LibRaw's guess at neutral, not the picture as shot.
        Assert.Contains("-w", Arguments());
        Assert.DoesNotContain("-a", Arguments());
    }

    [Fact]
    public void Auto_white_balance_swaps_the_flag_rather_than_adding_one()
    {
        var args = Arguments(RawDevelopSettings.Default with { WhiteBalance = RawWhiteBalance.Auto });

        // Passing both would let dcraw pick, which is not a choice the user made.
        Assert.Contains("-a", args);
        Assert.DoesNotContain("-w", args);
    }

    [Theory]
    [InlineData(RawHighlightMode.Clip, "0")]
    [InlineData(RawHighlightMode.Unclip, "1")]
    [InlineData(RawHighlightMode.Blend, "2")]
    [InlineData(RawHighlightMode.Rebuild, "3")]
    public void Highlight_mode_maps_to_its_number(RawHighlightMode mode, string expected)
    {
        var args = Arguments(RawDevelopSettings.Default with { Highlights = mode });

        Assert.Equal(expected, args[args.IndexOf("-H") + 1]);
    }

    [Theory]
    [InlineData(RawQuality.Fast, "0")]
    [InlineData(RawQuality.Balanced, "2")]
    [InlineData(RawQuality.Best, "3")]
    public void Quality_maps_to_an_interpolation(RawQuality quality, string expected)
    {
        var args = Arguments(RawDevelopSettings.Default with { Quality = quality });

        Assert.Equal(expected, args[args.IndexOf("-q") + 1]);
    }

    [Fact]
    public void Noise_reduction_is_absent_unless_asked_for()
        => Assert.DoesNotContain("-fbdd", Arguments());

    [Theory]
    [InlineData(RawNoiseReduction.Light, "1")]
    [InlineData(RawNoiseReduction.Full, "2")]
    public void Noise_reduction_maps_to_fbdd(RawNoiseReduction level, string expected)
    {
        var args = Arguments(RawDevelopSettings.Default with { NoiseReduction = level });

        Assert.Equal(expected, args[args.IndexOf("-fbdd") + 1]);
    }

    [Fact]
    public void As_shot_passes_no_exposure_argument()
    {
        // An exposure shift of 1.0 is not quite a no-op in LibRaw, so "as shot" must mean silence.
        Assert.DoesNotContain("-aexpo", Arguments());
    }

    [Theory]
    [InlineData(1, "2")]
    [InlineData(-1, "0.5")]
    [InlineData(2, "4")]
    public void Exposure_stops_become_a_linear_multiplier(double stops, string expected)
    {
        var args = Arguments(RawDevelopSettings.Default with { ExposureStops = stops });

        Assert.Equal(expected, args[args.IndexOf("-aexpo") + 1]);
    }

    [Fact]
    public void The_exposure_multiplier_stays_within_what_libraw_accepts()
    {
        // LibRaw takes 0.25 to 8; a slider must not be able to produce an argument it refuses.
        Assert.Equal(8, (RawDevelopSettings.Default with { ExposureStops = 99 }).ExposureMultiplier);
        Assert.Equal(0.25, (RawDevelopSettings.Default with { ExposureStops = -99 }).ExposureMultiplier);
    }

    [Fact]
    public void Exposure_is_written_invariantly()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma decimal separator would make dcraw reject the argument.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
            var args = Arguments(RawDevelopSettings.Default with { ExposureStops = -1 });
            Assert.Equal("0.5", args[args.IndexOf("-aexpo") + 1]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Output_goes_to_stdout_in_srgb_with_the_file_last()
    {
        var args = Arguments();

        Assert.Equal("1", args[args.IndexOf("-o") + 1]);
        Assert.Equal("-", args[args.IndexOf("-Z") + 1]);
        Assert.Equal("/library/shot.raf", args[^1]);
    }

    [Fact]
    public void Defaults_are_the_file_as_the_camera_recorded_it()
    {
        var settings = RawDevelopSettings.Default;

        Assert.True(settings.IsDefault);
        Assert.Equal(RawWhiteBalance.Camera, settings.WhiteBalance);
        Assert.Equal(0, settings.ExposureStops);
        Assert.Equal(RawNoiseReduction.Off, settings.NoiseReduction);
    }

    [Fact]
    public void A_minimal_ppm_is_parsed_to_bgra()
    {
        // Two pixels: pure red then pure blue, as RGB.
        var ppm = "P6\n2 1\n255\n"u8.ToArray()
            .Concat<byte>([255, 0, 0, 0, 0, 255])
            .ToArray();

        var image = LibRawImageDecoder.ParsePpm(ppm);

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);

        // BGRA: the red pixel's blue channel is 0 and its red channel is 255.
        Assert.Equal([0, 0, 255, 255, 255, 0, 0, 255], image.Pixels);
    }

    [Fact]
    public void Comments_and_extra_whitespace_in_the_header_are_tolerated()
    {
        var ppm = "P6\n# written by something\n1  1\n255\n"u8.ToArray()
            .Concat<byte>([10, 20, 30])
            .ToArray();

        var image = LibRawImageDecoder.ParsePpm(ppm);

        Assert.NotNull(image);
        Assert.Equal([30, 20, 10, 255], image.Pixels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P5\n1 1\n255\n")]
    [InlineData("P6\n1 1\n65535\n")]
    [InlineData("P6\n1 1\n255\n")]
    public void Anything_unexpected_is_rejected_rather_than_misread(string header)
    {
        // Not a PPM, the greyscale variant, 16-bit, and a truncated body: none should produce a
        // half-decoded image.
        Assert.Null(LibRawImageDecoder.ParsePpm(System.Text.Encoding.ASCII.GetBytes(header)));
    }

    [Fact]
    public void Zero_dimensions_are_rejected()
        => Assert.Null(LibRawImageDecoder.ParsePpm("P6\n0 0\n255\n"u8));
}

public class CompositeRawDecoderTests
{
    private sealed class Stub(DecodedImage? result, bool available = true) : IRawDecoder
    {
        public int Calls { get; private set; }

        public bool IsAvailable => available;

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static readonly MediaFile Raw = new()
    {
        FullPath = "/library/shot.dng",
        FileName = "shot.dng",
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static DecodedImage Image(string renderer) => new([0, 0, 0, 0], 1, 1, renderer);

    private static CompositeRawDecoder Create(params IRawDecoder[] decoders)
        => new(decoders, NullLogger<CompositeRawDecoder>.Instance);

    [Fact]
    public async Task The_first_decoder_that_succeeds_wins()
    {
        var second = new Stub(Image("second"));
        var decoded = await Create(new Stub(Image(DecodedImage.LibRaw)), second).DevelopAsync(Raw);

        // LibRaw is first because it is the one the develop settings drive.
        Assert.Equal(DecodedImage.LibRaw, decoded!.Renderer);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task A_decoder_that_cannot_open_the_file_falls_through()
    {
        // The JPEG XL DNG case: LibRaw cannot unpack it, the platform can.
        var decoded = await Create(new Stub(null), new Stub(Image(DecodedImage.Platform))).DevelopAsync(Raw);

        Assert.Equal(DecodedImage.Platform, decoded!.Renderer);
    }

    [Fact]
    public async Task Nothing_is_returned_when_every_decoder_fails()
    {
        // The caller then falls back to the embedded preview rather than showing nothing.
        Assert.Null(await Create(new Stub(null), new Stub(null)).DevelopAsync(Raw));
    }

    [Fact]
    public void Unavailable_decoders_are_dropped_rather_than_tried()
    {
        var unavailable = new Stub(Image("no"), available: false);

        Assert.False(Create(unavailable).IsAvailable);
    }

    [Fact]
    public void With_no_decoders_at_all_raw_development_is_unavailable()
        => Assert.False(Create().IsAvailable);

    [Theory]
    [InlineData(6000, 4000)]     // 24MP
    [InlineData(8062, 3922)]     // the stitched panorama that prompted all this
    [InlineData(10000, 8000)]    // 80MP, exactly the ceiling
    public void Images_within_budget_are_rendered_at_full_size(int w, int h)
    {
        var (width, height) = ImageIoRawDecoder.FitWithinBudget(w, h);

        Assert.Equal(w, width);
        Assert.Equal(h, height);
    }

    [Fact]
    public void An_enormous_image_is_scaled_down_keeping_its_shape()
    {
        var (width, height) = ImageIoRawDecoder.FitWithinBudget(40000, 20000);

        Assert.True((long)width * height <= 80_000_000);
        Assert.Equal(2.0, width / (double)height, precision: 2);
    }
}
