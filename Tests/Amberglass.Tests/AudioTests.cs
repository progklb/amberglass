using Amberglass.Core.Interfaces;
using Amberglass.Preview.Audio;
using Amberglass.Preview.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class AudioFormatTests
{
    [Fact]
    public void The_default_format_is_consistent()
    {
        var format = AudioFormat.Default;

        // 16-bit stereo: four bytes a frame, and a second of it is that times the rate.
        Assert.Equal(4, format.BytesPerFrame);
        Assert.Equal(48_000 * 4, format.BytesPerSecond);
    }
}

public class FfmpegAudioArgumentTests
{
    private static List<string> Arguments(TimeSpan from)
        => [.. FfmpegAudioPlayer.BuildStartInfo("/usr/bin/ffmpeg", "/library/clip.mp4", from).ArgumentList];

    [Fact]
    public void Video_decoding_is_disabled()
    {
        // Decoding video here would duplicate work the frame source is already doing.
        Assert.Contains("-vn", Arguments(TimeSpan.Zero));
    }

    [Fact]
    public void The_output_matches_what_the_device_expects()
    {
        var args = Arguments(TimeSpan.Zero);

        Assert.Contains("s16le", args);
        Assert.Contains("48000", args);
        Assert.Contains("2", args);
    }

    [Fact]
    public void Seeking_happens_before_the_input()
    {
        var args = Arguments(TimeSpan.FromSeconds(42));

        // -ss after -i decodes and discards everything up to the position, which for a seek into a
        // long file is the difference between instant and several seconds.
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"), "-ss must precede -i");
        Assert.Equal("42", args[args.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void Fractional_positions_survive()
    {
        var args = Arguments(TimeSpan.FromSeconds(1.5));

        Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void The_position_is_written_invariantly()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma decimal separator would make ffmpeg reject the argument.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
            var args = Arguments(TimeSpan.FromSeconds(2.5));
            Assert.Equal("2.5", args[args.IndexOf("-ss") + 1]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}

public class AudioPlayerAvailabilityTests
{
    private sealed class StubLocator(bool available) : IFfmpegLocator
    {
        public bool IsAvailable => available;
        public string? FfmpegPath => available ? "/usr/bin/ffmpeg" : null;
        public string? FfprobePath => available ? "/usr/bin/ffprobe" : null;
    }

    private static FfmpegAudioPlayer Create(bool ffmpeg, IAudioOutput output)
        => new(new StubLocator(ffmpeg), output, NullLogger<FfmpegAudioPlayer>.Instance);

    [Fact]
    public void Without_an_output_backend_audio_is_unavailable()
    {
        // Windows and Linux land here: no backend, so nothing is decoded at all.
        using var player = Create(ffmpeg: true, new SilentAudioOutput());

        Assert.False(player.IsAvailable);
    }

    [Fact]
    public void Volume_round_trips_and_clamps()
    {
        using var player = Create(ffmpeg: true, new SilentAudioOutput());

        player.Volume = 0.5;
        Assert.Equal(0.5, player.Volume, precision: 3);

        player.Volume = 4;
        Assert.Equal(1, player.Volume);

        player.Volume = -1;
        Assert.Equal(0, player.Volume);
    }

    [Fact]
    public async Task Starting_without_a_backend_does_nothing_rather_than_throwing()
    {
        using var player = Create(ffmpeg: true, new SilentAudioOutput());

        await player.StartAsync("/library/clip.mp4", TimeSpan.Zero);
        await player.StopAsync();
    }

    [Fact]
    public async Task Stopping_when_never_started_is_safe()
    {
        using var player = Create(ffmpeg: false, new SilentAudioOutput());

        await player.StopAsync();
    }
}

public class AudioStreamDetectionTests
{
    private const string WithAudio = """
        {"streams":[
          {"codec_type":"video","width":1920,"height":1080,"avg_frame_rate":"25/1"},
          {"codec_type":"audio"}],
         "format":{"duration":"12.5"}}
        """;

    private const string VideoOnly = """
        {"streams":[
          {"codec_type":"video","width":1920,"height":1080,"avg_frame_rate":"25/1"}],
         "format":{"duration":"12.5"}}
        """;

    [Fact]
    public void An_audio_stream_is_detected()
        => Assert.True(FfprobeVideoInfoProvider.Parse(WithAudio)!.HasAudio);

    [Fact]
    public void A_silent_file_reports_no_audio()
        => Assert.False(FfprobeVideoInfoProvider.Parse(VideoOnly)!.HasAudio);

    [Fact]
    public void The_video_stream_is_still_found_when_it_is_not_first()
    {
        // Streams are no longer filtered to v:0, so the video one has to be picked out by type.
        const string audioFirst = """
            {"streams":[
              {"codec_type":"audio"},
              {"codec_type":"video","width":640,"height":480,"avg_frame_rate":"30/1"}],
             "format":{"duration":"3"}}
            """;

        var info = FfprobeVideoInfoProvider.Parse(audioFirst);

        Assert.NotNull(info);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public void A_file_with_no_video_stream_is_not_playable()
        => Assert.Null(FfprobeVideoInfoProvider.Parse("""{"streams":[{"codec_type":"audio"}],"format":{"duration":"3"}}"""));
}
