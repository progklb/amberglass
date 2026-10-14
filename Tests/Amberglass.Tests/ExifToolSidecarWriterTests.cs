using System.Security.Cryptography;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Metadata.ExifTool;
using Amberglass.Metadata.Xmp;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Integration tests against a real ExifTool. These are the tests that actually prove the Phase 3
/// safety promises, so they are worth the process spawn.
/// </summary>
public class ExifToolSidecarWriterTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Temp = new TempFolder();
            Host = new ExifToolHost(new RealExifTool.Locator(), NullLogger<ExifToolHost>.Instance);
            Reader = new ExifToolMetadataProvider(Host, NullLogger<ExifToolMetadataProvider>.Instance);
            Writer = new ExifToolSidecarWriter(Host, Reader, NullLogger<ExifToolSidecarWriter>.Instance);
        }

        public TempFolder Temp { get; }

        public ExifToolHost Host { get; }

        public ExifToolMetadataProvider Reader { get; }

        public ExifToolSidecarWriter Writer { get; }

        /// <summary>Writes a small real JPEG so ExifTool has something valid to work alongside.</summary>
        public MediaFile CreateJpeg(string name)
        {
            var path = Path.Combine(Temp.Path, name);

            using (var bitmap = new SKBitmap(64, 48))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.SlateGray);
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            }

            return MediaFile.FromFileInfo(new FileInfo(path));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temp.Dispose();
        }
    }

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Creates_a_sidecar_and_leaves_the_media_file_untouched()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG001.jpg");

        var hashBefore = HashOf(file.FullPath);
        var modifiedBefore = File.GetLastWriteTimeUtc(file.FullPath);

        var result = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Title = "Lioness at dawn", Rating = 4, Keywords = ["wildlife", "Namibia"] },
            new SidecarWriteOptions());

        Assert.True(result.Success, result.Error);

        var expectedSidecar = Path.Combine(fixture.Temp.Path, "IMG001.xmp");
        Assert.Equal(expectedSidecar, result.SidecarPath);
        Assert.True(File.Exists(expectedSidecar));

        // The whole point of the phase: ordinary metadata editing does not touch the original.
        Assert.Equal(hashBefore, HashOf(file.FullPath));
        Assert.Equal(modifiedBefore, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public async Task Written_values_can_be_read_back()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG002.jpg");

        var written = new EditableMetadata
        {
            Title = "Lioness at dawn",
            Description = "Early light on the plain",
            Headline = "Dawn patrol",
            Keywords = ["wildlife", "Namibia", "lioness"],
            Rating = 4,
            Label = "Green",
            Creator = "Kevin Baynham",
            Copyright = "(c) 2024 Kevin Baynham"
        };

        Assert.True((await fixture.Writer.WriteSidecarAsync(file, written, new SidecarWriteOptions())).Success);

        var reread = await fixture.Reader.ReadAsync(file);

        Assert.NotNull(reread?.Sidecar);
        Assert.True(written.ValueEquals(reread.Sidecar));
    }

    [Fact]
    public async Task Updating_a_sidecar_preserves_tags_the_application_does_not_manage()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG003.jpg");
        var sidecarPath = Path.Combine(fixture.Temp.Path, "IMG003.xmp");

        // A sidecar written by some other application, carrying a tag Amberglass knows nothing about.
        await RunExifToolAsync(
            "-overwrite_original",
            "-XMP-photoshop:City=Windhoek",
            "-XMP:Title=Original title",
            sidecarPath);

        Assert.Contains("Windhoek", await File.ReadAllTextAsync(sidecarPath));

        var result = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Title = "New title", Rating = 5 },
            new SidecarWriteOptions());

        Assert.True(result.Success, result.Error);

        var xmp = await File.ReadAllTextAsync(sidecarPath);

        // Safety principle: never discard metadata the application does not understand.
        Assert.Contains("Windhoek", xmp);
        Assert.Contains("New title", xmp);
        Assert.DoesNotContain("Original title", xmp);
    }

    [Fact]
    public async Task Clearing_a_field_removes_it_from_the_sidecar()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG004.jpg");

        await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Title = "Temporary", Rating = 3 },
            new SidecarWriteOptions());

        var cleared = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Rating = 3 },
            new SidecarWriteOptions());

        Assert.True(cleared.Success, cleared.Error);

        var reread = await fixture.Reader.ReadAsync(file);
        Assert.Null(reread!.Sidecar!.Title);
        Assert.Equal(3, reread.Sidecar.Rating);
    }

    /// <summary>
    /// Unsetting everything — stars, label, flag, keywords, text — and writing, with the read-back
    /// check on as the inspector has it. Nothing left to say is still a valid sidecar.
    /// </summary>
    [Fact]
    public async Task Clearing_every_field_writes_and_validates()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG014.jpg");

        var first = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Title = "Temporary", Rating = 4, Label = "Red", Flag = MediaFlag.Accepted, Keywords = ["a", "b"] },
            new SidecarWriteOptions { ValidateAfterWrite = true });
        Assert.True(first.Success, first.Error);

        var cleared = await fixture.Writer.WriteSidecarAsync(
            file,
            EditableMetadata.Empty,
            new SidecarWriteOptions { ValidateAfterWrite = true });

        Assert.True(cleared.Success, cleared.Error);
    }

    [Fact]
    public async Task Removed_keywords_disappear_rather_than_accumulating()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG005.jpg");

        var first = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Keywords = ["a", "b", "c"] },
            new SidecarWriteOptions());
        Assert.True(first.Success, first.Error);

        var second = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Keywords = ["b"] },
            new SidecarWriteOptions());
        Assert.True(second.Success, second.Error);

        var reread = await fixture.Reader.ReadAsync(file);
        Assert.Equal(["b"], reread!.Sidecar!.Keywords.ToArray());
    }

    [Fact]
    public async Task Writing_the_same_keywords_twice_does_not_duplicate_them()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG005b.jpg");
        var keywords = new EditableMetadata { Keywords = ["wildlife", "Namibia"] };

        Assert.True((await fixture.Writer.WriteSidecarAsync(file, keywords, new SidecarWriteOptions())).Success);
        var repeat = await fixture.Writer.WriteSidecarAsync(file, keywords, new SidecarWriteOptions());
        Assert.True(repeat.Success, repeat.Error);

        var reread = await fixture.Reader.ReadAsync(file);
        Assert.Equal(["wildlife", "Namibia"], reread!.Sidecar!.Keywords.ToArray());
    }

    [Fact]
    public async Task Clearing_every_keyword_removes_the_list()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG005c.jpg");

        await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Keywords = ["a", "b"] },
            new SidecarWriteOptions());

        var cleared = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Rating = 2 },
            new SidecarWriteOptions());

        Assert.True(cleared.Success, cleared.Error);

        var reread = await fixture.Reader.ReadAsync(file);
        Assert.Empty(reread!.Sidecar!.Keywords);
    }

    [Fact]
    public async Task A_multi_line_description_round_trips()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG006.jpg");

        // ExifTool argument files are line-based, so this value has to travel via a temp file.
        const string description = "First line\nSecond line\nThird line";

        var result = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Description = description },
            new SidecarWriteOptions());

        Assert.True(result.Success, result.Error);

        var reread = await fixture.Reader.ReadAsync(file);
        Assert.Equal(description, reread!.Sidecar!.Description?.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Updates_an_existing_appended_style_sidecar_rather_than_creating_a_second_one()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG007.jpg");

        // Some tools write VID001.MP4.xmp rather than VID001.xmp; that file must be the one updated.
        var appendedStyle = file.FullPath + ".xmp";
        await RunExifToolAsync("-overwrite_original", "-XMP:Title=Existing", appendedStyle);

        var result = await fixture.Writer.WriteSidecarAsync(
            file,
            new EditableMetadata { Title = "Updated" },
            new SidecarWriteOptions());

        Assert.True(result.Success, result.Error);
        Assert.Equal(appendedStyle, result.SidecarPath);
        Assert.False(File.Exists(Path.ChangeExtension(file.FullPath, ".xmp")));
        Assert.Contains("Updated", await File.ReadAllTextAsync(appendedStyle));
    }

    [Fact]
    public async Task Reports_a_failure_when_exiftool_is_unavailable()
    {
        var host = new ExifToolHost(new StubUnavailableLocator(), NullLogger<ExifToolHost>.Instance);
        await using var _ = host;

        using var temp = new TempFolder();
        var path = temp.CreateFile("IMG008.jpg");

        var reader = new ExifToolMetadataProvider(host, NullLogger<ExifToolMetadataProvider>.Instance);
        var writer = new ExifToolSidecarWriter(host, reader, NullLogger<ExifToolSidecarWriter>.Instance);

        var result = await writer.WriteSidecarAsync(
            MediaFile.FromFileInfo(new FileInfo(path)),
            new EditableMetadata { Title = "x" },
            new SidecarWriteOptions());

        Assert.False(result.Success);
        Assert.False(writer.IsAvailable);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
    }

    private sealed class StubUnavailableLocator : IExifToolLocator
    {
        public string? ExifToolPath => null;

        public bool IsAvailable => false;
    }

    [Theory]
    [InlineData("/library/IMG001.jpg", "/library/IMG001.xmp", true)]
    [InlineData("/library/IMG001.jpg", "/library/IMG001.jpg.xmp", true)]
    [InlineData("/library/IMG001.jpg", "/library/IMG001.jpg", false)]
    [InlineData("/library/IMG001.xmp", "/library/IMG001.xmp", false)]
    [InlineData("/library/IMG001.jpg", "/library/IMG001.JPG", false)]
    public void The_write_target_must_be_a_sidecar_and_never_the_media_file(
        string mediaPath,
        string sidecarPath,
        bool expected)
        => Assert.Equal(expected, ExifToolSidecarWriter.IsSafeSidecarTarget(mediaPath, sidecarPath));

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
}
