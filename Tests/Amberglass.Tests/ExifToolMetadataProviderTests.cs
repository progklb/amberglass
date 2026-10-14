using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Metadata.ExifTool;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class ExifToolMetadataProviderTests
{
    private sealed class StubLocator(string? path) : IExifToolLocator
    {
        public string? ExifToolPath { get; } = path;

        public bool IsAvailable => ExifToolPath is not null;
    }

    private static ExifToolHost CreateHost(FakeExifTool fake)
        => new(new StubLocator(fake.Path_), NullLogger<ExifToolHost>.Instance);

    private static ExifToolMetadataProvider CreateProvider(ExifToolHost host)
        => new(host, NullLogger<ExifToolMetadataProvider>.Instance);

    private static MediaFile MediaFileFor(string path) => MediaFile.FromFileInfo(new FileInfo(path));

    [Fact]
    public async Task Maps_editable_camera_and_raw_tags()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG001.jpg");

        using var fake = new FakeExifTool(FakeExifTool.SampleJson(imagePath));
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.NotNull(metadata);

        var editable = metadata.Effective;
        Assert.Equal("Lioness at dawn", editable.Title);
        Assert.Equal("Early light on the plain", editable.Description);
        Assert.Equal(["wildlife", "Namibia", "lioness"], editable.Keywords.ToArray());
        Assert.Equal(4, editable.Rating);
        Assert.Equal("Green", editable.Label);
        Assert.Equal("Kevin Baynham", editable.Creator);
        Assert.Equal("Dawn patrol", editable.Headline);

        Assert.Equal("Canon EOS R5", metadata.Camera.Camera);
        Assert.Equal("RF100-500mm F4.5-7.1 L IS USM", metadata.Camera.Lens);
        Assert.Equal("800", metadata.Camera.Iso);
        Assert.Equal("1/1250", metadata.Camera.ShutterSpeed);
        Assert.Equal("f/7.1", metadata.Camera.Aperture);

        Assert.NotEmpty(metadata.RawTags);
        Assert.Contains(metadata.RawTags, t => t.QualifiedName == "XMP:Title");
    }

    [Fact]
    public async Task Does_not_repeat_the_manufacturer_in_the_camera_name()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG002.jpg");

        // Model already starts with the make, as most cameras report it.
        var json = $$"""
            [{"SourceFile": "{{imagePath}}", "EXIF:Make": "Canon", "EXIF:Model": "Canon EOS R5"}]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.Equal("Canon EOS R5", metadata!.Camera.Camera);
    }

    [Fact]
    public async Task Joins_make_and_model_when_they_differ()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG003.jpg");

        var json = $$"""
            [{"SourceFile": "{{imagePath}}", "EXIF:Make": "NIKON CORPORATION", "EXIF:Model": "Z 8"}]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.Equal("NIKON CORPORATION Z 8", metadata!.Camera.Camera);
    }

    [Fact]
    public async Task Sidecar_values_take_precedence_over_embedded_ones()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG004.jpg");
        var sidecarPath = temp.CreateFile("IMG004.xmp");

        // The media file carries a title and rating; the sidecar overrides the rating only.
        var json = $$"""
            [
              {"SourceFile": "{{imagePath}}", "XMP:Title": "Embedded title", "XMP:Rating": 2},
              {"SourceFile": "{{sidecarPath}}", "XMP:Rating": 5}
            ]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.NotNull(metadata);
        Assert.True(metadata.HasSidecar);
        Assert.Equal(sidecarPath, metadata.SidecarPath);

        // Sidecar wins where it has a value; the embedded title survives because the sidecar is silent on it.
        Assert.Equal(5, metadata.Effective.Rating);
        Assert.Equal("Embedded title", metadata.Effective.Title);

        // Both layers stay separately available for Phase 3 conflict detection.
        Assert.Equal(2, metadata.Embedded.Rating);
        Assert.Equal(5, metadata.Sidecar!.Rating);
    }

    /// <summary>
    /// The sidecar is the layer this app writes, so its groups lead the All tags list rather than
    /// sorting in among the file's own EXIF, File and MakerNotes groups.
    /// </summary>
    [Fact]
    public async Task Sidecar_tags_are_listed_before_the_file_s_own()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG005.jpg");
        var sidecarPath = temp.CreateFile("IMG005.xmp");

        var json = $$"""
            [
              {"SourceFile": "{{imagePath}}", "EXIF:Make": "Fujifilm", "XMP:Rating": 2},
              {"SourceFile": "{{sidecarPath}}", "XMP:Rating": 5}
            ]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.Equal(["Sidecar:XMP", "EXIF", "XMP"], metadata!.RawTags.Select(t => t.Group).ToArray());
    }

    /// <summary>
    /// Both copies of a tag whose field the file and sidecar disagree on are marked, so the two
    /// values can be found in either half of All tags. Tags outside the editable fields are not,
    /// however much they differ.
    /// </summary>
    [Fact]
    public async Task Tags_behind_a_disagreeing_field_are_marked_on_both_sides()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG006.jpg");
        var sidecarPath = temp.CreateFile("IMG006.xmp");

        var json = $$"""
            [
              {"SourceFile": "{{imagePath}}", "XMP:Rating": 0, "XMP:Title": "Same", "File:FileSize": "2 MB"},
              {"SourceFile": "{{sidecarPath}}", "XMP:Rating": 4, "XMP:Title": "Same", "File:FileSize": "3 kB"}
            ]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        var marked = metadata!.RawTags.Where(t => t.Differs).Select(t => t.QualifiedName).ToArray();
        Assert.Equal(["Sidecar:XMP:Rating", "XMP:Rating"], marked);
    }

    [Fact]
    public async Task Reports_no_sidecar_when_none_exists()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG005.jpg");

        using var fake = new FakeExifTool(FakeExifTool.SampleJson(imagePath));
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.False(metadata!.HasSidecar);
        Assert.Null(metadata.Sidecar);
    }

    [Fact]
    public async Task Video_metadata_is_only_populated_for_videos()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var videoPath = temp.CreateFile("CLIP.mp4");

        var json = $$"""
            [{
              "SourceFile": "{{videoPath}}",
              "QuickTime:CompressorName": "H.265",
              "QuickTime:ImageWidth": 3840,
              "QuickTime:ImageHeight": 2160,
              "QuickTime:VideoFrameRate": 25,
              "QuickTime:Duration": "0:00:12",
              "QuickTime:AudioChannels": 2
            }]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(videoPath));

        Assert.Equal("H.265", metadata!.Video.Codec);
        Assert.Equal("3840 × 2160", metadata.Video.Resolution);
        Assert.Equal("25", metadata.Video.FrameRate);
        Assert.Equal("0:00:12", metadata.Video.Duration);
        Assert.Equal("2", metadata.Video.AudioChannels);
    }

    [Fact]
    public async Task Keywords_fall_back_to_iptc_when_xmp_has_none()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG006.jpg");

        var json = $$"""
            [{"SourceFile": "{{imagePath}}", "IPTC:Keywords": ["motorcycle", "travel"]}]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.Equal(["motorcycle", "travel"], metadata!.Effective.Keywords.ToArray());
    }

    [Fact]
    public async Task A_single_keyword_returned_as_a_string_still_parses()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG007.jpg");

        // ExifTool returns a bare string rather than an array when there is only one keyword.
        var json = $$"""
            [{"SourceFile": "{{imagePath}}", "XMP:Subject": "solo"}]
            """;

        using var fake = new FakeExifTool(json);
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        var metadata = await provider.ReadAsync(MediaFileFor(imagePath));

        Assert.Equal(["solo"], metadata!.Effective.Keywords.ToArray());
    }

    [Fact]
    public async Task Unparsable_output_yields_null_rather_than_throwing()
    {
        if (!FakeExifTool.IsSupported)
        {
            return;
        }

        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG008.jpg");

        using var fake = new FakeExifTool("this is not json");
        await using var host = CreateHost(fake);
        var provider = CreateProvider(host);

        Assert.Null(await provider.ReadAsync(MediaFileFor(imagePath)));
    }

    [Fact]
    public async Task Provider_is_unavailable_without_exiftool()
    {
        using var temp = new TempFolder();
        var imagePath = temp.CreateFile("IMG009.jpg");

        await using var host = new ExifToolHost(new StubLocator(null), NullLogger<ExifToolHost>.Instance);
        var provider = new ExifToolMetadataProvider(host, NullLogger<ExifToolMetadataProvider>.Instance);

        Assert.False(provider.IsAvailable);
        Assert.Null(await provider.ReadAsync(MediaFileFor(imagePath)));
    }
}
