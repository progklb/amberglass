namespace Amberglass.Core.Models;

/// <summary>
/// Technical capture metadata. Read-only in the UI — it describes what the camera did and is not
/// the application's to rewrite. Values are kept as ExifTool's formatted strings so that
/// "1/250" and "f/2.8" display exactly as a photographer expects.
/// </summary>
public sealed record CameraInfo
{
    public static readonly CameraInfo Empty = new();

    public string? Camera { get; init; }

    public string? Lens { get; init; }

    public string? Iso { get; init; }

    public string? ShutterSpeed { get; init; }

    public string? Aperture { get; init; }

    public string? FocalLength { get; init; }

    public string? CaptureDate { get; init; }

    public string? Gps { get; init; }

    public string? Orientation { get; init; }

    public bool IsEmpty =>
        Camera is null && Lens is null && Iso is null && ShutterSpeed is null && Aperture is null &&
        FocalLength is null && CaptureDate is null && Gps is null && Orientation is null;
}
