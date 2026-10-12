using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Metadata.ExifTool;
using Amberglass.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

public class RawThumbnailGeneratorTests
{
    private sealed class StubExtractor(byte[]? preview, bool available = true) : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable { get; } = available;

        public int Calls { get; private set; }

        public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(preview is null ? null : new EmbeddedPreview(preview));
        }
    }

    /// <summary>Stands in for LibRaw/ImageIO: hands back flat BGRA of the requested size.</summary>
    private sealed class StubDecoder(int width, int height, bool available = true) : IRawDecoder
    {
        public bool IsAvailable { get; } = available;

        public int Calls { get; private set; }

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (width <= 0)
            {
                return Task.FromResult<DecodedImage?>(null);
            }

            var pixels = new byte[(long)width * height * 4];
            Array.Fill(pixels, (byte)0x80);
            return Task.FromResult<DecodedImage?>(new DecodedImage(pixels, width, height));
        }
    }

    private static readonly StubDecoder NoDecoder = new(0, 0, available: false);

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.OrangeRed);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static MediaFile RawFile(string name = "/library/IMG001.CR3") => new()
    {
        FullPath = name,
        FileName = Path.GetFileName(name),
        MediaType = MediaTypeRegistry.GetMediaType(name),
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static RawThumbnailGenerator Create(IEmbeddedPreviewExtractor extractor, IRawDecoder? raw = null)
        => new(extractor, raw ?? NoDecoder, NullLogger<RawThumbnailGenerator>.Instance);

    [Theory]
    [InlineData("/library/IMG001.CR3", true)]
    [InlineData("/library/IMG001.NEF", true)]
    [InlineData("/library/IMG001.ARW", true)]
    [InlineData("/library/IMG001.RAF", true)]
    [InlineData("/library/IMG001.DNG", true)]
    [InlineData("/library/IMG001.tif", true)]
    // Skia handles these directly, so the RAW path must not claim them.
    [InlineData("/library/IMG001.jpg", false)]
    [InlineData("/library/IMG001.png", false)]
    // Not an image at all.
    [InlineData("/library/CLIP.mp4", false)]
    public void Handles_images_skia_cannot_decode(string path, bool expected)
        => Assert.Equal(expected, Create(new StubExtractor([])).CanHandle(RawFile(path)));

    [Fact]
    public void Declines_everything_when_neither_route_is_available()
    {
        var generator = Create(new StubExtractor(null, available: false));

        Assert.False(generator.CanHandle(RawFile()));
    }

    /// <summary>Developing is a route in its own right, not only a fallback from an extraction.</summary>
    [Fact]
    public void Handles_raws_when_only_the_decoder_is_available()
    {
        var generator = Create(new StubExtractor(null, available: false), new StubDecoder(4000, 3000));

        Assert.True(generator.CanHandle(RawFile()));
    }

    [Theory]
    // Exactly the size asked for, and comfortably larger: no reason to develop.
    [InlineData(320, 320, true)]
    [InlineData(2400, 1600, true)]
    // Short of the target but within the stretch allowance — a 256px preview is fine for a grid tile.
    [InlineData(256, 320, true)]
    [InlineData(800, 1600, true)]
    // Beyond it. This is the stitched-panorama case: 256px asked to fill a 1600px preview pane.
    [InlineData(256, 1600, false)]
    [InlineData(799, 1600, false)]
    [InlineData(0, 320, false)]
    public void Judges_whether_a_preview_is_big_enough(int previewEdge, int requested, bool adequate)
        => Assert.Equal(adequate, RawThumbnailGenerator.IsPreviewAdequate(previewEdge, requested));

    /// <summary>
    /// The reported case: a DNG whose only ordinary preview is 256x125, shown in the 1600px preview
    /// pane. Upscaling that is the mush the develop exists to avoid.
    /// </summary>
    [Fact]
    public async Task Develops_when_the_embedded_preview_is_too_small_for_the_requested_size()
    {
        var decoder = new StubDecoder(8062, 3922);
        var generator = Create(new StubExtractor(EncodeJpeg(256, 125)), decoder);

        var bytes = await generator.GenerateAsync(RawFile(), 1600);

        Assert.NotNull(bytes);
        Assert.Equal(1, decoder.Calls);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(1600, decoded.Width);
    }

    /// <summary>
    /// The same file at grid size must not develop: that is what keeps a folder of panoramas
    /// browsable.
    /// </summary>
    [Fact]
    public async Task Uses_a_slightly_small_preview_rather_than_developing()
    {
        var decoder = new StubDecoder(8062, 3922);
        var generator = Create(new StubExtractor(EncodeJpeg(256, 125)), decoder);

        var bytes = await generator.GenerateAsync(RawFile(), 320);

        Assert.NotNull(bytes);
        Assert.Equal(0, decoder.Calls);

        // Never upscaled: the preview is rendered at the size it is.
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(256, decoded.Width);
    }

    [Fact]
    public async Task Develops_when_there_is_no_embedded_preview_at_all()
    {
        var decoder = new StubDecoder(6000, 4000);
        var generator = Create(new StubExtractor(null), decoder);

        var bytes = await generator.GenerateAsync(RawFile(), 320);

        Assert.NotNull(bytes);
        Assert.Equal(1, decoder.Calls);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(213, decoded.Height);
    }

    /// <summary>A failed develop must not lose the preview we already had, small though it is.</summary>
    [Fact]
    public async Task Falls_back_to_the_small_preview_when_developing_fails()
    {
        var decoder = new StubDecoder(0, 0);
        var generator = Create(new StubExtractor(EncodeJpeg(256, 125)), decoder);

        var bytes = await generator.GenerateAsync(RawFile(), 1600);

        Assert.NotNull(bytes);
        Assert.Equal(1, decoder.Calls);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(256, decoded.Width);
    }

    [Fact]
    public async Task Renders_the_extracted_preview_down_to_the_requested_size()
    {
        var generator = Create(new StubExtractor(EncodeJpeg(2400, 1600)));

        var bytes = await generator.GenerateAsync(RawFile(), 320);

        Assert.NotNull(bytes);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(213, decoded.Height);
    }

    [Fact]
    public async Task Returns_null_when_the_file_has_no_embedded_preview()
    {
        var generator = Create(new StubExtractor(null));

        Assert.Null(await generator.GenerateAsync(RawFile(), 320));
    }

    [Fact]
    public async Task Returns_null_when_the_preview_is_not_a_decodable_image()
    {
        var generator = Create(new StubExtractor("not an image"u8.ToArray()));

        Assert.Null(await generator.GenerateAsync(RawFile(), 320));
    }
}

public class ExifToolPreviewExtractorTests
{
    private static ExifToolPreviewExtractor Create()
        => Create(new RealExifTool.Locator());

    private static ExifToolPreviewExtractor Create(IExifToolLocator locator)
        => new(
            locator,
            new ExifToolHost(locator, NullLogger<ExifToolHost>.Instance),
            NullLogger<ExifToolPreviewExtractor>.Instance);

    private static async Task RunExifToolAsync(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        await process.WaitForExitAsync();
    }

    private static string WriteJpeg(string path, int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public async Task Extracts_an_embedded_preview_as_jpeg_bytes()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();

        // A JPEG carrying an embedded thumbnail stands in for a RAW: the extraction path through
        // ExifTool's binary output is identical.
        var host = WriteJpeg(Path.Combine(temp.Path, "HOST.jpg"), 400, 300, SKColors.SlateGray);
        var embedded = WriteJpeg(Path.Combine(temp.Path, "embedded.jpg"), 160, 120, SKColors.OrangeRed);
        await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", host);

        var preview = await Create().ExtractAsync(MediaFile.FromFileInfo(new FileInfo(host)));

        Assert.NotNull(preview);

        // Binary data must survive the process pipe intact.
        Assert.Equal(0xFF, preview.Jpeg[0]);
        Assert.Equal(0xD8, preview.Jpeg[1]);

        using var decoded = SKBitmap.Decode(preview.Jpeg);
        Assert.Equal(160, decoded.Width);
        Assert.Equal(120, decoded.Height);
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_preview_to_extract()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();
        var plain = WriteJpeg(Path.Combine(temp.Path, "PLAIN.jpg"), 200, 150, SKColors.SlateGray);

        Assert.Null(await Create().ExtractAsync(MediaFile.FromFileInfo(new FileInfo(plain))));
    }

    [Fact]
    public async Task Reports_unavailable_without_exiftool()
    {
        var extractor = Create(new UnavailableLocator());

        Assert.False(extractor.IsAvailable);
        Assert.Null(await extractor.ExtractAsync(new MediaFile
        {
            FullPath = "/library/IMG001.CR3",
            FileName = "IMG001.CR3",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        }));
    }

    private sealed class UnavailableLocator : IExifToolLocator
    {
        public string? ExifToolPath => null;

        public bool IsAvailable => false;
    }
}
