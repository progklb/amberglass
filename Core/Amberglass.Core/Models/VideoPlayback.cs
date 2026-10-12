namespace Amberglass.Core.Models;

/// <summary>
/// Playback resolution. The whole point of proxies: a 5.3K source should not be decoded at full
/// resolution just to look at it.
/// </summary>
public enum VideoQuality
{
    /// <summary>Decode the original file. No proxy, no disk cost, most CPU.</summary>
    Original = 0,
    P360 = 360,
    P480 = 480,
    P720 = 720
}

/// <summary>Technical facts needed to lay out a timeline and size a decode.</summary>
public sealed record VideoMediaInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio = false)
{
    public static readonly VideoMediaInfo Unknown = new(TimeSpan.Zero, 0, 0, 0);

    public bool IsUsable => Width > 0 && Height > 0 && Duration > TimeSpan.Zero;

    /// <summary>Frame duration, falling back to 25fps when the rate is unknown or nonsensical.</summary>
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1.0 / (FrameRate > 0.1 ? FrameRate : 25));
}

/// <summary>A generated low-resolution stand-in for a source video.</summary>
public sealed record VideoProxy(string SourcePath, VideoQuality Quality, string ProxyPath, VideoMediaInfo Info);

/// <summary>One decoded frame, as tightly packed BGRA ready to blit.</summary>
/// <param name="Buffer">
/// Rented from a pool and only valid until <see cref="Return"/> is called — playback allocates
/// several megabytes per frame, so recycling matters more than immutability here.
/// </param>
public sealed record VideoFrame(byte[] Buffer, int Length, int Width, int Height, TimeSpan Position, Action<byte[]> Return)
    : IDisposable
{
    private int _returned;

    /// <summary>
    /// Returns the buffer to the pool. Idempotent, because returning the same array twice hands the
    /// same memory to two future renters — a corruption bug that would be miserable to track down.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            Return(Buffer);
        }
    }
}
