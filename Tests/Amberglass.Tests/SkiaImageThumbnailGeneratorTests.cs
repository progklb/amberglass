using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

public class SkiaImageThumbnailGeneratorTests
{
    private static readonly SkiaImageThumbnailGenerator Generator =
        new(NullLogger<SkiaImageThumbnailGenerator>.Instance);

    private static MediaFile WriteJpeg(TempFolder temp, string name, int width, int height)
    {
        var path = Path.Combine(temp.Path, name);

        using (var bitmap = new SKBitmap(width, height))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.CornflowerBlue);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }

        return MediaFile.FromFileInfo(new FileInfo(path));
    }

    [Fact]
    public async Task Produces_a_thumbnail_bounded_by_the_requested_edge()
    {
        using var temp = new TempFolder();
        var file = WriteJpeg(temp, "landscape.jpg", 1600, 800);

        var bytes = await Generator.GenerateAsync(file, 320);

        Assert.NotNull(bytes);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(160, decoded.Height);
    }

    [Fact]
    public async Task Preserves_orientation_of_portrait_images()
    {
        using var temp = new TempFolder();
        var file = WriteJpeg(temp, "portrait.jpg", 600, 1200);

        var bytes = await Generator.GenerateAsync(file, 300);

        Assert.NotNull(bytes);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(150, decoded.Width);
        Assert.Equal(300, decoded.Height);
    }

    [Fact]
    public async Task Does_not_upscale_images_smaller_than_the_target()
    {
        using var temp = new TempFolder();
        var file = WriteJpeg(temp, "small.jpg", 100, 50);

        var bytes = await Generator.GenerateAsync(file, 320);

        Assert.NotNull(bytes);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(100, decoded.Width);
        Assert.Equal(50, decoded.Height);
    }

    [Fact]
    public async Task Returns_null_for_an_undecodable_file()
    {
        using var temp = new TempFolder();
        var path = temp.CreateFile("broken.jpg", "this is not a jpeg");

        var bytes = await Generator.GenerateAsync(MediaFile.FromFileInfo(new FileInfo(path)), 320);

        Assert.Null(bytes);
    }

    [Fact]
    public void Declines_raw_and_video_files()
    {
        using var temp = new TempFolder();
        var raw = MediaFile.FromFileInfo(new FileInfo(temp.CreateFile("photo.cr3")));
        var video = MediaFile.FromFileInfo(new FileInfo(temp.CreateFile("clip.mp4")));

        Assert.False(Generator.CanHandle(raw));
        Assert.False(Generator.CanHandle(video));
    }
}
