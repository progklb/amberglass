using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class VideoMediaInfoTests
{
    [Fact]
    public void Frame_duration_follows_the_frame_rate()
    {
        var info = new VideoMediaInfo(TimeSpan.FromSeconds(10), 1920, 1080, 25);

        Assert.Equal(TimeSpan.FromMilliseconds(40), info.FrameDuration);
    }

    [Fact]
    public void Frame_duration_falls_back_when_the_rate_is_unknown()
    {
        // A zero rate would otherwise divide by zero and take the player with it.
        var info = new VideoMediaInfo(TimeSpan.FromSeconds(10), 1920, 1080, 0);

        Assert.Equal(TimeSpan.FromMilliseconds(40), info.FrameDuration);
    }

    [Theory]
    [InlineData(1920, 1080, 10, true)]
    [InlineData(0, 1080, 10, false)]
    [InlineData(1920, 0, 10, false)]
    [InlineData(1920, 1080, 0, false)]
    public void Usability_requires_dimensions_and_duration(int width, int height, int seconds, bool expected)
        => Assert.Equal(expected, new VideoMediaInfo(TimeSpan.FromSeconds(seconds), width, height, 25).IsUsable);
}

public class FfprobeParsingTests
{
    [Fact]
    public void Parses_dimensions_frame_rate_and_duration()
    {
        const string json = """
            {"streams":[{"width":3840,"height":2160,"avg_frame_rate":"30/1","r_frame_rate":"30/1"}],
             "format":{"duration":"12.000000"}}
            """;

        var info = FfprobeVideoInfoProvider.Parse(json);

        Assert.NotNull(info);
        Assert.Equal(3840, info.Width);
        Assert.Equal(2160, info.Height);
        Assert.Equal(30, info.FrameRate);
        Assert.Equal(TimeSpan.FromSeconds(12), info.Duration);
    }

    [Fact]
    public void Handles_ntsc_style_rational_frame_rates()
    {
        const string json = """
            {"streams":[{"width":1920,"height":1080,"avg_frame_rate":"30000/1001"}],
             "format":{"duration":"5"}}
            """;

        var info = FfprobeVideoInfoProvider.Parse(json);

        Assert.Equal(29.97, info!.FrameRate, 2);
    }

    [Fact]
    public void Falls_back_to_r_frame_rate_when_the_average_is_missing()
    {
        const string json = """
            {"streams":[{"width":1920,"height":1080,"avg_frame_rate":"0/0","r_frame_rate":"25/1"}],
             "format":{"duration":"5"}}
            """;

        Assert.Equal(25, FfprobeVideoInfoProvider.Parse(json)!.FrameRate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"streams":[]}""")]
    public void Unusable_output_yields_null(string json)
        => Assert.Null(FfprobeVideoInfoProvider.Parse(json));
}

public class FrameDecodeSizeTests
{
    [Fact]
    public void A_large_source_is_capped_at_720p_preserving_aspect()
    {
        var (width, height) = FfmpegFrameSource.DecodeSize(new VideoMediaInfo(TimeSpan.FromSeconds(1), 3840, 2160, 30));

        Assert.Equal(720, height);
        Assert.Equal(1280, width);
    }

    [Fact]
    public void A_small_source_is_not_upscaled()
    {
        var (width, height) = FfmpegFrameSource.DecodeSize(new VideoMediaInfo(TimeSpan.FromSeconds(1), 640, 360, 30));

        Assert.Equal(640, width);
        Assert.Equal(360, height);
    }

    [Fact]
    public void Dimensions_are_always_even()
    {
        // H.264 and most scalers require even dimensions; an odd width would fail the encode.
        var (width, height) = FfmpegFrameSource.DecodeSize(new VideoMediaInfo(TimeSpan.FromSeconds(1), 1919, 1081, 30));

        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }
}

public class VideoFrameTests
{
    [Fact]
    public void Disposing_returns_the_buffer_once()
    {
        var returned = 0;
        var frame = new VideoFrame(new byte[16], 16, 2, 2, TimeSpan.Zero, _ => returned++);

        frame.Dispose();
        frame.Dispose();

        // Returning the same pooled array twice hands identical memory to two future renters.
        Assert.Equal(1, returned);
    }
}

/// <summary>
/// Integration tests against real ffmpeg, covering the behaviour that only shows up against a real
/// encoder: proxy caching, "do not upscale", and Original meaning no cache is written at all.
/// </summary>
public class VideoProxyServiceTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Temp = new TempFolder();
            Paths = new TestPaths(Temp.Path);
            var locator = new FfmpegLocator(NullLogger<FfmpegLocator>.Instance);
            Info = new FfprobeVideoInfoProvider(locator, NullLogger<FfprobeVideoInfoProvider>.Instance);
            Service = new FfmpegVideoProxyService(locator, Info, Paths, NullLogger<FfmpegVideoProxyService>.Instance);
            Available = locator.IsAvailable;
        }

        public TempFolder Temp { get; }

        public TestPaths Paths { get; }

        public FfprobeVideoInfoProvider Info { get; }

        public FfmpegVideoProxyService Service { get; }

        public bool Available { get; }

        /// <summary>Renders a tiny real clip so ffmpeg has something valid to work with.</summary>
        public MediaFile CreateClip(string name, int width, int height, int seconds = 2)
        {
            var path = Path.Combine(Temp.Path, name);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = new FfmpegLocator(NullLogger<FfmpegLocator>.Instance).FfmpegPath!,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            foreach (var argument in (string[])
                     [
                         "-hide_banner", "-loglevel", "error",
                         "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=25",
                         "-t", seconds.ToString(), "-pix_fmt", "yuv420p", "-y", path
                     ])
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            process.WaitForExit();

            return MediaFile.FromFileInfo(new FileInfo(path));
        }

        public int ProxyFileCount => Directory.Exists(Paths.VideoProxyCacheRoot)
            ? Directory.GetFiles(Paths.VideoProxyCacheRoot, "*.mp4", SearchOption.AllDirectories).Length
            : 0;

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task Original_quality_uses_the_source_and_writes_no_cache()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("orig.mp4", 640, 360);

        var proxy = await fixture.Service.GetProxyAsync(file, VideoQuality.Original);

        Assert.NotNull(proxy);
        Assert.Equal(file.FullPath, proxy.ProxyPath);

        // "Optional proxies" means choosing Original must not write anything to disk.
        Assert.Equal(0, fixture.ProxyFileCount);
    }

    [Fact]
    public async Task A_proxy_is_generated_and_reused()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("big.mp4", 1920, 1080);

        var first = await fixture.Service.GetProxyAsync(file, VideoQuality.P360);
        Assert.NotNull(first);
        Assert.NotEqual(file.FullPath, first.ProxyPath);
        Assert.True(File.Exists(first.ProxyPath));
        Assert.Equal(360, first.Info.Height);
        Assert.Equal(1, fixture.ProxyFileCount);

        var second = await fixture.Service.GetProxyAsync(file, VideoQuality.P360);

        Assert.Equal(first.ProxyPath, second!.ProxyPath);
        Assert.Equal(1, fixture.ProxyFileCount);
        Assert.True(fixture.Service.HasProxy(file, VideoQuality.P360));
    }

    [Fact]
    public async Task A_source_smaller_than_the_proxy_is_not_upscaled()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("small.mp4", 640, 360);

        // Encoding a 360p source "up" to 720p would cost time and produce a larger file than the
        // original for no benefit.
        var proxy = await fixture.Service.GetProxyAsync(file, VideoQuality.P720);

        Assert.Equal(VideoQuality.Original, proxy!.Quality);
        Assert.Equal(file.FullPath, proxy.ProxyPath);
        Assert.Equal(0, fixture.ProxyFileCount);
    }

    [Fact]
    public async Task Progress_is_reported_while_encoding()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("progress.mp4", 1920, 1080, seconds: 3);

        var reports = new List<double>();
        await fixture.Service.GetProxyAsync(file, VideoQuality.P360, new Progress<double>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.All(reports, p => Assert.InRange(p, 0, 1));
        Assert.Equal(1, reports[^1], 3);
    }

    [Fact]
    public async Task Different_qualities_get_different_cache_entries()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("multi.mp4", 1920, 1080);

        var low = await fixture.Service.GetProxyAsync(file, VideoQuality.P360);
        var mid = await fixture.Service.GetProxyAsync(file, VideoQuality.P480);

        Assert.NotEqual(low!.ProxyPath, mid!.ProxyPath);
        Assert.Equal(2, fixture.ProxyFileCount);
    }

    [Fact]
    public async Task Frames_can_be_decoded_from_a_generated_proxy()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("decode.mp4", 1280, 720);
        var proxy = await fixture.Service.GetProxyAsync(file, VideoQuality.P360);

        var frames = new FfmpegFrameSource(
            new FfmpegLocator(NullLogger<FfmpegLocator>.Instance),
            NullLogger<FfmpegFrameSource>.Instance);

        using var frame = await frames.GetFrameAsync(proxy!.ProxyPath, proxy.Info, TimeSpan.FromSeconds(1));

        Assert.NotNull(frame);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Length);
        Assert.True(frame.Buffer.Length >= frame.Length);

        // A decoded frame of the test pattern must not be uniformly blank.
        Assert.Contains(frame.Buffer.Take(frame.Length), b => b is not (0 or 255));
    }

    [Fact]
    public async Task Streaming_yields_sequential_timestamps()
    {
        using var fixture = new Fixture();
        if (!fixture.Available)
        {
            return;
        }

        var file = fixture.CreateClip("stream.mp4", 640, 360, seconds: 2);
        var info = await fixture.Info.GetInfoAsync(file);

        var frames = new FfmpegFrameSource(
            new FfmpegLocator(NullLogger<FfmpegLocator>.Instance),
            NullLogger<FfmpegFrameSource>.Instance);

        var positions = new List<TimeSpan>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await foreach (var frame in frames.StreamAsync(file.FullPath, info!, TimeSpan.Zero, cts.Token))
        {
            positions.Add(frame.Position);
            frame.Dispose();

            if (positions.Count >= 10)
            {
                break;
            }
        }

        Assert.Equal(10, positions.Count);
        Assert.Equal(TimeSpan.Zero, positions[0]);

        // Timestamps must advance by one frame each, or the player's pacing has nothing to work with.
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] > positions[i - 1]);
        }
    }
}
