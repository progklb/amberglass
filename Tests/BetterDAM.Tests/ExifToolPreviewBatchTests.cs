using System.Diagnostics;
using BetterDAM.Core.Models;
using BetterDAM.Metadata.ExifTool;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// Preview extraction serves several files from one ExifTool command. The risk that buys is files
/// getting each other's pictures, so these are mostly about identity rather than speed.
/// </summary>
public class ExifToolPreviewBatchTests
{
    /// <summary>
    /// <c>-W</c> rebuilds the source path underneath the output folder, which is what keeps two files
    /// of the same name apart. If this and ExifTool ever disagree, every extraction comes back empty.
    /// </summary>
    [Fact]
    public void TheOutputPathMirrorsTheSourcePathUnderTheRoot()
    {
        var path = ExifToolPreviewExtractor.OutputPathFor("/tmp/batch", "/library/trip/IMG_0001.CR2", "PreviewImage");

        Assert.Equal(Path.GetFullPath("/tmp/batch/library/trip/IMG_0001__PreviewImage.jpg"), path);
    }

    [Fact]
    public void TwoFoldersHoldingTheSameFileNameGetDifferentOutputPaths()
    {
        var a = ExifToolPreviewExtractor.OutputPathFor("/tmp/b", "/library/monday/IMG_0001.CR2", "PreviewImage");
        var b = ExifToolPreviewExtractor.OutputPathFor("/tmp/b", "/library/tuesday/IMG_0001.CR2", "PreviewImage");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EachTagGetsItsOwnOutputPath()
    {
        var preview = ExifToolPreviewExtractor.OutputPathFor("/tmp/b", "/library/IMG_1.CR2", "PreviewImage");
        var thumb = ExifToolPreviewExtractor.OutputPathFor("/tmp/b", "/library/IMG_1.CR2", "ThumbnailImage");

        Assert.NotEqual(preview, thumb);
    }

    /// <summary>
    /// The one that would catch a real batching bug: every file is asked for at once, each carries a
    /// preview of its own size, and each must come back with its own.
    /// </summary>
    [Fact]
    public async Task EveryFileInABatchGetsItsOwnPreview()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();

        // Same file name in two folders, which is what the mirrored output path exists to handle,
        // and enough files to cross a batch boundary.
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        var hosts = new List<string>();

        foreach (var folder in new[] { "monday", "tuesday" })
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, folder));

            for (var i = 0; i < 20; i++)
            {
                var host = Path.Combine(temp.Path, folder, $"IMG_{i:D3}.jpg");
                File.WriteAllBytes(host, Jpeg(400, 300));

                // A preview whose width identifies the file it belongs to.
                var width = 60 + (folder == "monday" ? 0 : 100) + i;
                var embedded = Path.Combine(temp.Path, $"e_{folder}_{i}.jpg");
                File.WriteAllBytes(embedded, Jpeg(width, 40));

                await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", host);

                expected[host] = width;
                hosts.Add(host);
            }
        }

        var locator = new RealExifTool.Locator();
        await using var host2 = new ExifToolHost(locator, NullLogger<ExifToolHost>.Instance);
        var extractor = new ExifToolPreviewExtractor(locator, host2, NullLogger<ExifToolPreviewExtractor>.Instance);

        var previews = await Task.WhenAll(hosts.Select(h =>
            extractor.ExtractAsync(MediaFile.FromFileInfo(new FileInfo(h)))));

        for (var i = 0; i < hosts.Count; i++)
        {
            Assert.NotNull(previews[i]);

            using var decoded = SKBitmap.Decode(previews[i]!.Jpeg);
            Assert.Equal(expected[hosts[i]], decoded.Width);
        }
    }

    /// <summary>A file with no preview at all comes back null rather than holding up the batch.</summary>
    [Fact]
    public async Task AFileWithNoPreviewDoesNotDisturbTheOthers()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();

        var bare = Path.Combine(temp.Path, "BARE.jpg");
        File.WriteAllBytes(bare, Jpeg(300, 200));

        var carrying = Path.Combine(temp.Path, "CARRYING.jpg");
        File.WriteAllBytes(carrying, Jpeg(400, 300));
        var embedded = Path.Combine(temp.Path, "e.jpg");
        File.WriteAllBytes(embedded, Jpeg(123, 40));
        await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", carrying);

        var locator = new RealExifTool.Locator();
        await using var host = new ExifToolHost(locator, NullLogger<ExifToolHost>.Instance);
        var extractor = new ExifToolPreviewExtractor(locator, host, NullLogger<ExifToolPreviewExtractor>.Instance);

        var results = await Task.WhenAll(
            extractor.ExtractAsync(MediaFile.FromFileInfo(new FileInfo(bare))),
            extractor.ExtractAsync(MediaFile.FromFileInfo(new FileInfo(carrying))));

        Assert.Null(results[0]);
        Assert.NotNull(results[1]);

        using var decoded = SKBitmap.Decode(results[1]!.Jpeg);
        Assert.Equal(123, decoded.Width);
    }

    /// <summary>The extraction folder is temporary and must not survive the batch that made it.</summary>
    [Fact]
    public async Task TheExtractionFolderIsClearedAfterwards()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();
        var host0 = Path.Combine(temp.Path, "HOST.jpg");
        File.WriteAllBytes(host0, Jpeg(400, 300));
        var embedded = Path.Combine(temp.Path, "e.jpg");
        File.WriteAllBytes(embedded, Jpeg(160, 120));
        await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", host0);

        var locator = new RealExifTool.Locator();
        await using var host = new ExifToolHost(locator, NullLogger<ExifToolHost>.Instance);
        var extractor = new ExifToolPreviewExtractor(locator, host, NullLogger<ExifToolPreviewExtractor>.Instance);

        Assert.NotNull(await extractor.ExtractAsync(MediaFile.FromFileInfo(new FileInfo(host0))));

        // Asked of this file rather than by counting folders: other tests extract at the same time,
        // and a count would be measuring their comings and goings as much as this one's.
        var leaked = Directory
            .GetDirectories(Path.GetTempPath(), "betterdam-preview-*")
            .Where(folder => File.Exists(ExifToolPreviewExtractor.OutputPathFor(folder, host0, "ThumbnailImage")))
            .ToList();

        Assert.Empty(leaked);
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SlateGray);
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
