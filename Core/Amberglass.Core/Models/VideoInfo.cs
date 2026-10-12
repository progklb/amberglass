namespace Amberglass.Core.Models;

/// <summary>Technical video metadata. Read-only, like <see cref="CameraInfo"/>.</summary>
public sealed record VideoInfo
{
    public static readonly VideoInfo Empty = new();

    public string? Codec { get; init; }

    public string? Resolution { get; init; }

    public string? FrameRate { get; init; }

    public string? Duration { get; init; }

    public string? Bitrate { get; init; }

    public string? ColourSpace { get; init; }

    public string? HdrInfo { get; init; }

    public string? AudioCodec { get; init; }

    public string? AudioChannels { get; init; }

    public string? AudioSampleRate { get; init; }

    public bool IsEmpty =>
        Codec is null && Resolution is null && FrameRate is null && Duration is null &&
        Bitrate is null && ColourSpace is null && HdrInfo is null && AudioCodec is null &&
        AudioChannels is null && AudioSampleRate is null;
}
