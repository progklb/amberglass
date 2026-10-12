namespace Amberglass.Core.Models;

/// <summary>What to do with pixels the sensor clipped.</summary>
public enum RawHighlightMode
{
    /// <summary>Clip them to white. Fastest, and what the camera JPEG effectively did.</summary>
    Clip = 0,

    /// <summary>Leave them unclipped, which reads as pink but shows there is data there.</summary>
    Unclip = 1,

    /// <summary>Blend clipped and unclipped, the usual compromise.</summary>
    Blend = 2,

    /// <summary>Reconstruct from the unclipped channels — the reason to open the RAW at all.</summary>
    Rebuild = 3
}

public enum RawWhiteBalance
{
    /// <summary>What the camera recorded: the picture as the photographer framed it.</summary>
    Camera,

    /// <summary>Averaged from the whole frame. Useful when the camera got it wrong.</summary>
    Auto
}

public enum RawNoiseReduction
{
    Off = 0,
    Light = 1,
    Full = 2
}

/// <summary>
/// How much work the demosaic does. Named for the decision rather than the algorithm, because the
/// choice being made is "am I culling or judging", not "do I want AHD".
/// </summary>
public enum RawQuality
{
    /// <summary>Linear interpolation. Roughly instant; enough to accept or reject a frame.</summary>
    Fast,

    /// <summary>PPG. A middle ground when a folder is being worked through.</summary>
    Balanced,

    /// <summary>AHD. Best fine detail, and several seconds an image.</summary>
    Best
}

/// <summary>
/// The develop settings applied when a RAW is rendered.
///
/// Deliberately a handful of choices rather than everything LibRaw exposes: these are the ones that
/// change what a photograph looks like when inspecting it. Anything that would need colour
/// management to be honest — output profiles, 16-bit — is left out until there is one.
/// </summary>
public sealed record RawDevelopSettings
{
    public static readonly RawDevelopSettings Default = new();

    public RawHighlightMode Highlights { get; init; } = RawHighlightMode.Clip;

    public RawWhiteBalance WhiteBalance { get; init; } = RawWhiteBalance.Camera;

    /// <summary>
    /// Exposure adjustment in stops, 0 being the file as shot. LibRaw takes a linear multiplier
    /// limited to 0.25–8, which is what bounds this to -2..+3.
    /// </summary>
    public double ExposureStops { get; init; }

    public RawNoiseReduction NoiseReduction { get; init; } = RawNoiseReduction.Off;

    public RawQuality Quality { get; init; } = RawQuality.Best;

    public const double MinExposureStops = -2;
    public const double MaxExposureStops = 3;

    /// <summary>True when nothing has been changed from the file as the camera recorded it.</summary>
    public bool IsDefault => this == Default;

    /// <summary>
    /// The linear multiplier LibRaw wants, from the stops the UI shows. Clamped rather than
    /// rejected: a slider should not be able to produce an argument the tool refuses.
    /// </summary>
    public double ExposureMultiplier => Math.Clamp(Math.Pow(2, ExposureStops), 0.25, 8);

    /// <summary>The interpolation number for <c>-q</c>.</summary>
    public int InterpolationCode => Quality switch
    {
        RawQuality.Fast => 0,
        RawQuality.Balanced => 2,
        _ => 3
    };
}
