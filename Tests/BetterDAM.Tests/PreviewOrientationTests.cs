using System.Diagnostics;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// A RAW's embedded preview is usually a bare JPEG with no EXIF of its own — the camera records which
/// way up the picture goes in the RAW around it. These cover carrying that orientation across to the
/// preview without turning a picture that has already been turned.
/// </summary>
public class PreviewOrientationTests
{
    /// <summary>
    /// The whole mapping rests on Skia numbering its origins exactly as EXIF numbers its orientations,
    /// which is a property of somebody else's library, so it is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void SkiaNumbersItsOriginsTheWayExifDoes()
    {
        Assert.Equal(1, (int)SKEncodedOrigin.TopLeft);
        Assert.Equal(2, (int)SKEncodedOrigin.TopRight);
        Assert.Equal(3, (int)SKEncodedOrigin.BottomRight);
        Assert.Equal(4, (int)SKEncodedOrigin.BottomLeft);
        Assert.Equal(5, (int)SKEncodedOrigin.LeftTop);
        Assert.Equal(6, (int)SKEncodedOrigin.RightTop);
        Assert.Equal(7, (int)SKEncodedOrigin.RightBottom);
        Assert.Equal(8, (int)SKEncodedOrigin.LeftBottom);
        Assert.Equal(SKEncodedOrigin.TopLeft, SKEncodedOrigin.Default);
    }

    /// <summary>A landscape preview out of a landscape RAW: the orientation has not been applied yet.</summary>
    private static EmbeddedPreview Raw(int orientation)
        => new([], orientation, 5184, 3456);

    [Theory]
    [InlineData(2, SKEncodedOrigin.TopRight)]
    [InlineData(3, SKEncodedOrigin.BottomRight)]
    [InlineData(5, SKEncodedOrigin.LeftTop)]
    [InlineData(6, SKEncodedOrigin.RightTop)]
    [InlineData(8, SKEncodedOrigin.LeftBottom)]
    public void AnUnmarkedPreviewTakesTheOrientationFromTheRawAroundIt(int orientation, SKEncodedOrigin expected)
        => Assert.Equal(
            expected,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 5184, 3456, Raw(orientation)));

    /// <summary>
    /// A preview that does carry EXIF describes itself, and must not then be turned a second time by
    /// the container's tag.
    /// </summary>
    [Fact]
    public void APreviewThatStatesItsOwnOriginKeepsIt()
        => Assert.Equal(
            SKEncodedOrigin.RightTop,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.RightTop, 3456, 5184, Raw(8)));

    [Fact]
    public void AnOrdinaryImageFileHasNoContainerToAskAndIsLeftAlone()
        => Assert.Equal(
            SKEncodedOrigin.Default,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 4000, 3000, fallback: null));

    [Fact]
    public void ARawThatSaysNothingAboutOrientationLeavesThePreviewAsItIs()
        => Assert.Equal(
            SKEncodedOrigin.Default,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 5184, 3456, new EmbeddedPreview([])));

    /// <summary>1 is "already upright", so it is a no-op rather than a transform.</summary>
    [Fact]
    public void OrientationOneIsNothingToDo()
        => Assert.Equal(
            SKEncodedOrigin.Default,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 5184, 3456, Raw(1)));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void AnOrientationOutsideTheStandardEightIsIgnored(int orientation)
        => Assert.Equal(
            SKEncodedOrigin.Default,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 5184, 3456, Raw(orientation)));

    /// <summary>
    /// The trap: a writer that rotated the preview's pixels but left no EXIF on it. The quarter turn
    /// shows in the aspect no longer matching the RAW's stored aspect, and turning it again would lay
    /// an upright picture on its side.
    /// </summary>
    [Fact]
    public void APreviewSomebodyAlreadyTurnedIsNotTurnedAgain()
        => Assert.Equal(
            SKEncodedOrigin.Default,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 3456, 5184, Raw(8)));

    /// <summary>
    /// A portrait sensor is not an already-rotated preview. The aspects agree, so the turn still applies.
    /// </summary>
    [Fact]
    public void APortraitPreviewOfAPortraitSourceStillTurns()
        => Assert.Equal(
            SKEncodedOrigin.LeftBottom,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 3456, 5184, new EmbeddedPreview([], 8, 3456, 5184)));

    /// <summary>
    /// Mirrors and the half turn do not exchange the axes, so there is no trace to read and no guess
    /// to make: they are applied as the RAW states, whatever the aspect.
    /// </summary>
    [Fact]
    public void AMirrorAppliesEvenWhenTheAspectsDisagree()
        => Assert.Equal(
            SKEncodedOrigin.TopRight,
            SkiaThumbnailRenderer.OriginFor(SKEncodedOrigin.Default, 3456, 5184, Raw(2)));

    /// <summary>
    /// The bug as reported: a landscape preview from a RAW tagged "Rotate 270 CW" came out landscape.
    /// Through the renderer rather than the rule, so the rotation is proved to actually happen.
    /// </summary>
    [Fact]
    public void TheRenderedThumbnailOfASidewaysFrameComesOutPortrait()
    {
        var jpeg = EncodeJpeg(400, 300);

        using var withoutOrientation = new MemoryStream(jpeg);
        var asIs = SkiaThumbnailRenderer.Render(withoutOrientation, 320, CancellationToken.None);
        Assert.NotNull(asIs);
        using var landscape = SKBitmap.Decode(asIs);
        Assert.True(landscape.Width > landscape.Height);

        using var withOrientation = new MemoryStream(jpeg);
        var turned = SkiaThumbnailRenderer.Render(
            withOrientation, 320, CancellationToken.None, new EmbeddedPreview(jpeg, 8, 400, 300));

        Assert.NotNull(turned);
        using var portrait = SKBitmap.Decode(turned);
        Assert.True(
            portrait.Height > portrait.Width,
            $"expected a portrait thumbnail, got {portrait.Width}x{portrait.Height}");
    }

    /// <summary>
    /// End to end through the real ExifTool: the orientation has to survive being read off the
    /// container by the extractor, not merely be handled correctly once it is in hand.
    /// </summary>
    [Fact]
    public async Task TheExtractorBringsTheContainersOrientationBackWithThePreview()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();

        // A JPEG with an embedded thumbnail and an orientation tag stands in for a RAW: the embedded
        // JPEG carries no EXIF of its own, which is the case that matters.
        var host = Path.Combine(temp.Path, "HOST.jpg");
        File.WriteAllBytes(host, EncodeJpeg(400, 300));
        var embedded = Path.Combine(temp.Path, "embedded.jpg");
        File.WriteAllBytes(embedded, EncodeJpeg(160, 120));

        await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", "-Orientation#=8", host);

        var locator = new RealExifTool.Locator();
        await using var exifToolHost = new ExifToolHost(locator, NullLogger<ExifToolHost>.Instance);
        var extractor = new ExifToolPreviewExtractor(
            locator, exifToolHost, NullLogger<ExifToolPreviewExtractor>.Instance);

        var preview = await extractor.ExtractAsync(MediaFile.FromFileInfo(new FileInfo(host)));

        Assert.NotNull(preview);
        Assert.Equal(8, preview.ExifOrientation);
        Assert.Equal(400, preview.SourceWidth);
        Assert.Equal(300, preview.SourceHeight);

        // And the preview itself genuinely has no orientation of its own, which is why it was needed.
        using var stream = new MemoryStream(preview.Jpeg);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.True(codec.EncodedOrigin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft);
    }

    /// <summary>
    /// macOS ImageIO decodes a RAW as the file stores it and leaves the orientation as a property, so
    /// the decoder turns the pixels itself. These cover that turn rather than the preview path.
    /// </summary>
    [Fact]
    public void OrientingAlreadyDecodedPixelsExchangesTheAxesForAQuarterTurn()
    {
        var image = Bgra(4, 2);

        var turned = SkiaThumbnailRenderer.Orient(image, 8);

        Assert.Equal(2, turned.Width);
        Assert.Equal(4, turned.Height);
        Assert.Equal(2 * 4 * 4, turned.Pixels.Length);
    }

    [Fact]
    public void AHalfTurnKeepsTheShapeAndReversesThePixels()
    {
        // Two pixels, red then blue, as BGRA.
        var image = new DecodedImage([0, 0, 255, 255, 255, 0, 0, 255], 2, 1);

        var turned = SkiaThumbnailRenderer.Orient(image, 3);

        Assert.Equal(2, turned.Width);
        Assert.Equal(1, turned.Height);
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 0, 255, 255], turned.Pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(9)]
    public void OrientingToNothingHandsBackTheSameImage(int orientation)
    {
        var image = Bgra(4, 2);

        Assert.Same(image, SkiaThumbnailRenderer.Orient(image, orientation));
    }

    [Fact]
    public void OrientingATruncatedBufferIsRefusedRatherThanRead()
    {
        var short_ = new DecodedImage([0, 0, 0, 0], 4, 2);

        Assert.Same(short_, SkiaThumbnailRenderer.Orient(short_, 8));
    }

    private static DecodedImage Bgra(int width, int height)
        => new(new byte[width * height * 4], width, height);

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SlateGray);
            // Something asymmetric, so a wrong transform is a wrong picture and not just wrong numbers.
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(0, 0, width / 4f, height / 2f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static async Task RunExifToolAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
    }
}
