using Amberglass.Core.Interfaces;

namespace Amberglass.Preview.Audio;

/// <summary>
/// The output used where no platform backend exists yet — Windows and Linux.
///
/// It reports itself unavailable rather than pretending to work, so the player skips decoding
/// entirely instead of running ffmpeg to throw the samples away, and the UI can say audio is
/// unavailable rather than leaving someone wondering why a video is silent.
/// </summary>
public sealed class SilentAudioOutput : IAudioOutput
{
    public bool IsAvailable => false;

    public void Start(AudioFormat format)
    {
    }

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
