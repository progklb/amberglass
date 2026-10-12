namespace Amberglass.Core.Interfaces;

/// <summary>
/// The PCM format audio is decoded to and handed to the sound device.
///
/// Fixed rather than negotiated: ffmpeg will resample anything to this on the way out, so the
/// output device only ever has to handle one format.
/// </summary>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public static readonly AudioFormat Default = new(48_000, 2, 16);

    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    public int BytesPerSecond => SampleRate * BytesPerFrame;
}

/// <summary>
/// A sound device that plays raw PCM.
///
/// Deliberately dumb: it takes bytes and plays them. Decoding, timing and volume all live above it,
/// so a new platform only has to implement "somewhere to put samples".
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>False where no backend exists for the platform; playback then runs silent.</summary>
    bool IsAvailable { get; }

    void Start(AudioFormat format);

    /// <summary>
    /// Queues samples for playback. Blocks while the device's queue is full, which is what paces
    /// the decoder — audio plays at exactly its natural rate, so the device is the clock.
    /// </summary>
    void Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken);

    void Stop();
}

/// <summary>Plays the audio track of a media file, in step with the video showing beside it.</summary>
public interface IAudioPlayer : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>0 is silent, 1 is unattenuated. Takes effect immediately, mid-playback.</summary>
    double Volume { get; set; }

    /// <summary>
    /// Starts playing <paramref name="path"/> from <paramref name="from"/>. Returns once playback
    /// has started, not once it has finished. Does nothing when the file has no audio track.
    /// </summary>
    Task StartAsync(string path, TimeSpan from, CancellationToken cancellationToken = default);

    Task StopAsync();
}
