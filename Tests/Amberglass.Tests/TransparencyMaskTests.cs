using System.IO.Compression;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

public class TransparencyMaskTests
{
    private static byte[] Pixels(int width, int height, byte value = 200)
        => Enumerable.Repeat(value, width * height * 4).ToArray();

    private static byte[] Pixel(byte[] pixels, int width, int x, int y)
        => pixels.AsSpan(((y * width) + x) * 4, 4).ToArray();

    [Fact]
    public void Clears_the_dead_space_and_leaves_the_picture()
    {
        var mask = new TransparencyMask([0, 255, 255, 0], 2, 2);
        var pixels = Pixels(2, 2);

        Assert.True(mask.Apply(pixels, 2, 2, 8));

        Assert.Equal(new byte[4], Pixel(pixels, 2, 0, 0));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(pixels, 2, 1, 0));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(pixels, 2, 0, 1));
        Assert.Equal(new byte[4], Pixel(pixels, 2, 1, 1));
    }

    /// <summary>
    /// The edge of a stitched panorama is soft, and premultiplied pixels have every channel scaled
    /// by coverage — alpha included, or the colour would be dimmed while staying fully opaque.
    /// </summary>
    [Fact]
    public void A_soft_edge_scales_every_channel()
    {
        var mask = new TransparencyMask([128], 1, 1);
        var pixels = Pixels(1, 1);

        mask.Apply(pixels, 1, 1, 4);

        Assert.All(pixels, channel => Assert.Equal(100, channel));
    }

    /// <summary>An embedded preview's dead space is white, so its soft edge is cleared outright.</summary>
    [Fact]
    public void A_hard_mask_clears_the_soft_edge_too()
    {
        var mask = new TransparencyMask([128], 1, 1);
        var pixels = Pixels(1, 1);

        mask.Apply(pixels, 1, 1, 4, hard: true);

        Assert.All(pixels, channel => Assert.Equal(0, channel));
    }

    /// <summary>A reduced-resolution mask is stretched over a bigger picture rather than refused.</summary>
    [Fact]
    public void A_smaller_mask_is_scaled_to_the_picture()
    {
        var mask = new TransparencyMask([0, 255, 0, 255], 2, 2);
        var pixels = Pixels(8, 8);

        Assert.True(mask.Apply(pixels, 8, 8, 32));

        Assert.Equal(new byte[4], Pixel(pixels, 8, 0, 4));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(pixels, 8, 7, 4));
    }

    /// <summary>
    /// Orientation 6 turns the stored picture a quarter clockwise to stand it up, so the mask's
    /// left-hand column ends up along the top of what is shown.
    /// </summary>
    [Fact]
    public void The_mask_turns_with_the_picture()
    {
        var mask = new TransparencyMask([0, 255], 2, 1, Orientation: 6);
        var pixels = Pixels(1, 2);

        Assert.True(mask.Apply(pixels, 1, 2, 4));

        Assert.Equal(new byte[4], Pixel(pixels, 1, 0, 0));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(pixels, 1, 0, 1));
    }

    /// <summary>A mask that cannot be this picture's must not punch holes in it.</summary>
    [Fact]
    public void A_mask_of_the_wrong_shape_is_not_applied()
    {
        var mask = new TransparencyMask(new byte[4 * 1], 4, 1);
        var pixels = Pixels(2, 2);

        Assert.False(mask.Apply(pixels, 2, 2, 8));
        Assert.All(pixels, channel => Assert.Equal(200, channel));
    }

    // ---- Reading masks out of a DNG ----------------------------------------------------------------

    private static DngTransparencyMaskReader Reader() => new(NullLogger<DngTransparencyMaskReader>.Instance);

    private sealed record SubImage(uint SubfileType, int Width, int Height, byte[] Data, ushort Compression = 1);

    /// <summary>A DNG with an orientation in IFD0 and each sub-image as a single 8-bit strip.</summary>
    private static byte[] Dng(int orientation, params SubImage[] images)
        => TestDng.Build(
            new TestDng.Image([], new TestDng.Entry(274, TestDng.Short, (uint)orientation)),
            images.Select(image => new TestDng.Image(
                image.Data,
                new TestDng.Entry(254, TestDng.Long, image.SubfileType),
                new TestDng.Entry(256, TestDng.Long, (uint)image.Width),
                new TestDng.Entry(257, TestDng.Long, (uint)image.Height),
                new TestDng.Entry(258, TestDng.Short, 8),
                new TestDng.Entry(259, TestDng.Short, image.Compression),
                new TestDng.Entry(278, TestDng.Long, (uint)image.Height))).ToArray());

    private static byte[] Filled(int count, byte value) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void Reads_the_smallest_mask_that_covers_the_target()
    {
        var dng = Dng(
            1,
            new SubImage(0, 16, 8, Filled(16 * 8 * 3, 9)), // the picture itself, not a mask
            new SubImage(4, 16, 8, Filled(16 * 8, 1)),
            new SubImage(5, 8, 4, Filled(8 * 4, 2)),
            new SubImage(5, 4, 2, Filled(4 * 2, 3)));

        var mask = Reader().Read(new MemoryStream(dng), targetEdge: 6);

        Assert.NotNull(mask);
        Assert.Equal((8, 4), (mask.Width, mask.Height));
        Assert.All(mask.Values, value => Assert.Equal(2, value));
    }

    [Fact]
    public void Falls_back_to_the_largest_mask_when_none_is_big_enough()
    {
        var dng = Dng(1, new SubImage(4, 16, 8, Filled(16 * 8, 1)), new SubImage(5, 4, 2, Filled(4 * 2, 3)));

        var mask = Reader().Read(new MemoryStream(dng), targetEdge: 10_000);

        Assert.Equal((16, 8), (mask!.Width, mask.Height));
    }

    [Fact]
    public void Reads_a_deflated_mask_and_the_orientation()
    {
        var values = Enumerable.Range(0, 6 * 3).Select(i => (byte)(i * 10)).ToArray();

        using var compressed = new MemoryStream();
        using (var deflater = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(values);
        }

        var dng = Dng(8, new SubImage(4, 6, 3, compressed.ToArray(), Compression: 8));

        var mask = Reader().Read(new MemoryStream(dng), targetEdge: 6);

        Assert.Equal(values, mask!.Values);
        Assert.Equal(8, mask.Orientation);
    }

    [Fact]
    public void A_dng_without_a_mask_has_none()
    {
        var dng = Dng(1, new SubImage(0, 4, 2, Filled(4 * 2 * 3, 9)));

        Assert.Null(Reader().Read(new MemoryStream(dng), targetEdge: 4));
    }

    /// <summary>JPEG XL needs a platform decoder; without one the mask is skipped, not guessed at.</summary>
    [Fact]
    public void A_jpeg_xl_mask_without_a_decoder_is_skipped()
    {
        var dng = Dng(1, new SubImage(4, 4, 2, [0xFF, 0x0A, 0, 0], Compression: 52546));

        Assert.Null(Reader().Read(new MemoryStream(dng), targetEdge: 4));
    }

    [Fact]
    public void Only_dngs_are_opened_and_a_damaged_one_is_shrugged_off()
    {
        using var temp = new TempFolder();
        var jpeg = temp.CreateFile("photo.jpg", "not a tiff");
        var broken = temp.CreateFile("pano.dng", "not a tiff either");

        Assert.Null(Reader().Read(MediaFile.FromFileInfo(new FileInfo(jpeg)), 320));
        Assert.Null(Reader().Read(MediaFile.FromFileInfo(new FileInfo(broken)), 320));
    }

    // ---- Through the thumbnail pipeline ------------------------------------------------------------

    private sealed class WhitePreview : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable => true;

        public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            using var bitmap = new SKBitmap(64, 32);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
            return Task.FromResult<EmbeddedPreview?>(new EmbeddedPreview(data.ToArray()));
        }
    }

    private sealed class NoDevelop : IRawDecoder
    {
        public bool IsAvailable => false;

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<DecodedImage?>(null);
    }

    /// <summary>Left half empty, right half picture — the shape of a panorama's ragged end.</summary>
    private sealed class HalfMask : ITransparencyMaskReader
    {
        public TransparencyMask? Read(MediaFile file, int targetEdge) => new([0, 255], 2, 1);
    }

    /// <summary>
    /// The bug as reported: a panorama's thumbnail with bright white where the picture is not. The
    /// white has to come out black, and the picture beside it has to stay as it was.
    /// </summary>
    [Fact]
    public async Task A_panorama_thumbnail_shows_black_where_its_preview_was_white()
    {
        var generator = new RawThumbnailGenerator(
            new WhitePreview(), new NoDevelop(), NullLogger<RawThumbnailGenerator>.Instance, new HalfMask());

        var file = new MediaFile
        {
            FullPath = "/library/pano.dng",
            FileName = "pano.dng",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

        var bytes = await generator.GenerateAsync(file, 64);
        using var thumbnail = SKBitmap.Decode(bytes);

        Assert.True(thumbnail.GetPixel(4, 16).Red < 16, "The dead space is not black.");
        Assert.True(thumbnail.GetPixel(60, 16).Red > 240, "The picture was darkened.");
    }
}
