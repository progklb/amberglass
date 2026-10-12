namespace Amberglass.Core.Models;

/// <summary>Which way round a picture is. Square is its own answer rather than a kind of landscape.</summary>
public enum MediaOrientation
{
    Landscape = 0,
    Portrait = 1,
    Square = 2
}

/// <summary>
/// A picture's size in pixels, the right way up.
/// </summary>
/// <remarks>
/// The stored width and height are not the answer on their own. A camera held on its side records
/// the sensor's own dimensions — landscape numbers — and an EXIF tag saying to turn the result a
/// quarter turn. Every viewer in the world honours that tag, so the file is a portrait to everyone
/// looking at it while its numbers say otherwise. <see cref="From"/> is where that is reconciled,
/// once, so that nothing downstream has to remember.
/// </remarks>
public readonly record struct ImageDimensions(int Width, int Height)
{
    public MediaOrientation Orientation =>
        Width == Height ? MediaOrientation.Square
        : Height > Width ? MediaOrientation.Portrait
        : MediaOrientation.Landscape;

    /// <summary>
    /// Dimensions as displayed, from what the file stores and what its orientation tag says to do
    /// with it. Null when either measurement is missing or nonsensical.
    /// </summary>
    /// <param name="orientation">
    /// ExifTool's formatted orientation, e.g. "Rotate 270 CW". Taken as text rather than as the
    /// underlying number because that is what comes back without <c>-n</c>, and asking for the
    /// number separately would mean a second convention to keep in step with the first.
    /// </param>
    public static ImageDimensions? From(int? width, int? height, string? orientation)
    {
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        return SwapsAxes(orientation)
            ? new ImageDimensions(height.Value, width.Value)
            : new ImageDimensions(width.Value, height.Value);
    }

    /// <summary>
    /// Whether the orientation turns the picture through a quarter turn, which exchanges its width
    /// and height.
    ///
    /// The four EXIF orientations that do are 5 to 8, and all four are spelled with 90 or 270 in
    /// them — "Rotate 90 CW", "Mirror horizontal and rotate 270 CW". The four that do not are 1 to
    /// 4, whose wordings contain no angle but 180. So the digits are enough, and they hold for the
    /// mirrored cases too, which a simple "starts with Rotate" test would miss.
    /// </summary>
    private static bool SwapsAxes(string? orientation)
        => orientation is not null
            && (orientation.Contains("90", StringComparison.Ordinal)
                || orientation.Contains("270", StringComparison.Ordinal));
}
