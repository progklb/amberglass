using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

public class DngPreviewExtractorTests
{
    private const uint LinearRaw = 34892;

    /// <summary>What ExifTool would have extracted, and how often it was asked.</summary>
    private sealed class StubExtractor(byte[]? jpeg) : IEmbeddedPreviewExtractor
    {
        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(jpeg is null ? null : new EmbeddedPreview(jpeg));
        }
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static TestDng.Entry[] Tags(uint subfileType, int width, int height, uint photometric, ushort compression, int samples = 3)
        =>
        [
            new(254, TestDng.Long, subfileType),
            new(256, TestDng.Long, (uint)width),
            new(257, TestDng.Long, (uint)height),
            new(258, TestDng.Short, Enumerable.Repeat(8u, samples).ToArray()),
            new(259, TestDng.Short, compression),
            new(262, TestDng.Short, photometric),
            new(277, TestDng.Short, (uint)samples),
            new(278, TestDng.Long, (uint)height)
        ];

    /// <summary>
    /// The shape of a Lightroom panorama: a token preview in IFD0, the main raw image, a larger
    /// rendered preview and a reduced-resolution raw copy among the SubIFDs.
    /// </summary>
    private static byte[] Panorama(int previewWidth, int previewHeight, ushort previewCompression = 7, int orientation = 1)
        => TestDng.Build(
            new TestDng.Image(
                Jpeg(16, 8),
                [.. Tags(1, 16, 8, photometric: 6, compression: 7), new(274, TestDng.Short, (uint)orientation)]),
            new TestDng.Image(new byte[4], Tags(0, 4000, 2000, LinearRaw, compression: 1)),
            new TestDng.Image(
                previewCompression == 7 ? Jpeg(previewWidth, previewHeight) : [0xFF, 0x0A, 0, 0],
                Tags(1, previewWidth, previewHeight, photometric: 2, previewCompression)),
            new TestDng.Image(new byte[4], Tags(1, 2048, 1024, LinearRaw, compression: 1)));

    private static (DngPreviewExtractor Extractor, StubExtractor Inner) Create(byte[]? exifToolPreview = null)
    {
        var inner = new StubExtractor(exifToolPreview);
        return (new DngPreviewExtractor(inner, NullLogger<DngPreviewExtractor>.Instance), inner);
    }

    private static MediaFile Write(TempFolder temp, string name, byte[] contents)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, contents);
        return MediaFile.FromFileInfo(new FileInfo(path));
    }

    private static (int Width, int Height) SizeOf(EmbeddedPreview preview)
    {
        using var bitmap = SKBitmap.Decode(preview.Jpeg);
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// The bug in practice: the pane developed a whole panorama because the only preview it was
    /// given was IFD0's 256px one, while a 1024px rendering sat unused in the same file.
    /// </summary>
    [Fact]
    public async Task Takes_the_largest_rendered_preview_rather_than_ifd0s()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "pano.dng", Panorama(64, 32, orientation: 6));

        var preview = await Create().Extractor.ExtractAsync(file);

        Assert.NotNull(preview);
        Assert.Equal((64, 32), SizeOf(preview));
        Assert.Equal(6, preview.ExifOrientation);
        Assert.Equal((4000, 2000), (preview.SourceWidth, preview.SourceHeight));
    }

    /// <summary>
    /// Reduced-resolution raw data shares the preview's subfile type but needs a develop to look at;
    /// taking it for a preview would hand undemosaiced sensor values to an image decoder.
    /// </summary>
    [Fact]
    public async Task Ignores_reduced_resolution_raw_data()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "pano.dng", Panorama(64, 32));

        var preview = await Create().Extractor.ExtractAsync(file);

        Assert.NotEqual((2048, 1024), SizeOf(preview!));
    }

    [Fact]
    public async Task A_large_enough_preview_does_not_ask_exiftool()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "pano.dng", Panorama(DngPreviewExtractor.SufficientEdge, 512));
        var (extractor, inner) = Create(Jpeg(32, 16));

        await extractor.ExtractAsync(file);

        Assert.Equal(0, inner.Calls);
    }

    /// <summary>A small DNG preview may not be all the file has; ExifTool's wins if it is larger.</summary>
    [Fact]
    public async Task A_larger_exiftool_preview_wins_over_a_small_own_one()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "pano.dng", Panorama(64, 32));

        var preview = await Create(Jpeg(200, 100)).Extractor.ExtractAsync(file);

        Assert.Equal((200, 100), SizeOf(preview!));
    }

    /// <summary>Without a JPEG XL decoder the larger preview cannot be read, so the next one is used.</summary>
    [Fact]
    public async Task An_unreadable_preview_falls_back_to_the_next_largest()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "pano.dng", Panorama(64, 32, previewCompression: 52546));

        var preview = await Create().Extractor.ExtractAsync(file);

        Assert.Equal((16, 8), SizeOf(preview!));
    }

    [Fact]
    public async Task Other_raw_formats_go_straight_to_exiftool()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "frame.raf", Panorama(64, 32));
        var (extractor, inner) = Create(Jpeg(40, 20));

        var preview = await extractor.ExtractAsync(file);

        Assert.Equal(1, inner.Calls);
        Assert.Equal((40, 20), SizeOf(preview!));
    }
}
