using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Metadata.ExifTool;
using Amberglass.Metadata.Xmp;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Cull flags, against a real ExifTool.
///
/// These run the actual round trip rather than asserting on argument lists, because the whole value
/// of the feature is that another application can read what this one writes. An assertion about the
/// arguments would have passed just as happily while ExifTool refused the value — which is exactly
/// what <c>XMP-photomech:Tagged</c> does without its <c>#</c> suffix.
/// </summary>
public class MediaFlagTests : IAsyncDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ExifToolHost _host;
    private readonly ExifToolMetadataProvider _reader;
    private readonly ExifToolSidecarWriter _writer;

    public MediaFlagTests()
    {
        _host = new ExifToolHost(new RealExifTool.Locator(), NullLogger<ExifToolHost>.Instance);
        _reader = new ExifToolMetadataProvider(_host, NullLogger<ExifToolMetadataProvider>.Instance);
        _writer = new ExifToolSidecarWriter(_host, _reader, NullLogger<ExifToolSidecarWriter>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private MediaFile CreateJpeg(string name)
    {
        var path = Path.Combine(_temp.Path, name);

        using (var bitmap = new SKBitmap(32, 24))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 80))
        using (var stream = File.OpenWrite(path))
        {
            data.SaveTo(stream);
        }

        return MediaFile.FromFileInfo(new FileInfo(path));
    }

    private async Task<EditableMetadata> RoundTrip(string name, EditableMetadata metadata)
    {
        var file = CreateJpeg(name);

        var result = await _writer.WriteSidecarAsync(file, metadata, new SidecarWriteOptions());
        Assert.True(result.Success, result.Error);

        var read = await _reader.ReadAsync(file);
        Assert.NotNull(read);

        return read.Effective;
    }

    [Fact]
    public async Task An_accepted_flag_survives_a_round_trip()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("pick.jpg", new EditableMetadata { Flag = MediaFlag.Accepted, Rating = 4 });

        Assert.Equal(MediaFlag.Accepted, effective.Flag);

        // Accepting says nothing about the stars, so they are left alone.
        Assert.Equal(4, effective.Rating);
    }

    /// <summary>
    /// Rejecting has to take the rating over, because Adobe expresses rejection <i>as</i> a rating of
    /// -1. The stars are not representable alongside it, and the reader must not read that -1 back as
    /// a real rating.
    /// </summary>
    [Fact]
    public async Task A_rejected_flag_becomes_a_negative_rating_and_reads_back_as_rejected()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("reject.jpg", new EditableMetadata { Flag = MediaFlag.Rejected, Rating = 4 });

        Assert.Equal(MediaFlag.Rejected, effective.Flag);
        Assert.Null(effective.Rating);
    }

    [Fact]
    public async Task No_flag_writes_no_flag()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("none.jpg", new EditableMetadata { Rating = 3 });

        Assert.Null(effective.Flag);
        Assert.Equal(3, effective.Rating);
    }

    /// <summary>
    /// A file rejected in Bridge carries only a rating of -1. Before this, the rating was clamped to
    /// zero — which lost the rejection and invented a nought-star rating nobody had given.
    /// </summary>
    [Fact]
    public async Task A_rejection_made_elsewhere_is_understood()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var file = CreateJpeg("bridge.jpg");
        var sidecar = Path.Combine(_temp.Path, "bridge.xmp");

        // Written the way another application would, with nothing but Adobe's convention in it —
        // driving ExifTool directly rather than through this application's own writer.
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            ArgumentList = { "-o", sidecar, "-XMP:Rating=-1", file.FullPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            await process.WaitForExitAsync();
        }

        Assert.True(File.Exists(sidecar));

        var read = await _reader.ReadAsync(file);

        Assert.NotNull(read);
        Assert.Equal(MediaFlag.Rejected, read.Effective.Flag);
        Assert.Null(read.Effective.Rating);
    }

    /// <summary>
    /// Writes a sidecar the way Lightroom 9 does: the pick flag as <c>xmpDM:good</c>, True for a
    /// pick and False for a reject, and a rating of 0 beside it — so none of the older conventions
    /// is present to fall back on.
    /// </summary>
    private async Task<MediaMetadata> ReadLightroomSidecar(string name, string good)
    {
        var file = CreateJpeg(name);
        var sidecar = Path.Combine(_temp.Path, Path.ChangeExtension(name, ".xmp"));

        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            ArgumentList = { "-o", sidecar, $"-XMP-xmpDM:Good={good}", "-XMP:Rating=0", file.FullPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            await process.WaitForExitAsync();
        }

        Assert.True(File.Exists(sidecar));

        var read = await _reader.ReadAsync(file);
        Assert.NotNull(read);
        return read;
    }

    /// <summary>
    /// The reported fault: a folder culled in Lightroom showed no flags at all. Lightroom writes
    /// its flag as <c>xmpDM:good</c> and a rating of 0, which none of the three conventions read
    /// until now recognised.
    /// </summary>
    [Fact]
    public async Task A_rejection_made_in_Lightroom_is_understood()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var read = await ReadLightroomSidecar("lr-reject.jpg", "False");

        Assert.Equal(MediaFlag.Rejected, read.Effective.Flag);
    }

    [Fact]
    public async Task A_pick_made_in_Lightroom_is_understood()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var read = await ReadLightroomSidecar("lr-pick.jpg", "True");

        Assert.Equal(MediaFlag.Accepted, read.Effective.Flag);
    }

    /// <summary>
    /// The other direction: a flag set here has to be one Lightroom will read back, so the sidecar
    /// carries its convention alongside the others. Checked in the file itself, because the reader
    /// would find the digiKam property first and prove nothing about this one.
    /// </summary>
    [Theory]
    [InlineData(MediaFlag.Accepted, "True")]
    [InlineData(MediaFlag.Rejected, "False")]
    public async Task A_flag_set_here_is_written_in_Lightrooms_convention_too(MediaFlag flag, string expected)
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var file = CreateJpeg($"to-lr-{flag}.jpg");
        var result = await _writer.WriteSidecarAsync(file, new EditableMetadata { Flag = flag }, new SidecarWriteOptions());
        Assert.True(result.Success, result.Error);

        var xml = await File.ReadAllTextAsync(result.SidecarPath!);

        // Lightroom writes the property as an attribute and ExifTool as an element; in RDF they are
        // the same statement, and Lightroom reads both.
        Assert.Matches($"xmpDM:good(=\"{expected}\"|>{expected}<)", xml);
    }

    [Fact]
    public async Task Clearing_a_flag_clears_Lightrooms_convention_too()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var file = CreateJpeg("clear-lr.jpg");
        var first = await _writer.WriteSidecarAsync(file, new EditableMetadata { Flag = MediaFlag.Accepted }, new SidecarWriteOptions());
        Assert.True(first.Success, first.Error);

        var second = await _writer.WriteSidecarAsync(file, new EditableMetadata { Rating = 2 }, new SidecarWriteOptions());
        Assert.True(second.Success, second.Error);

        var xml = await File.ReadAllTextAsync(second.SidecarPath!);

        Assert.DoesNotContain("xmpDM:good", xml);
    }
}
