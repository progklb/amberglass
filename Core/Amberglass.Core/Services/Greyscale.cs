namespace Amberglass.Core.Services;

/// <summary>
/// Turns colour pixels grey for the viewer's black-and-white preview.
///
/// A viewing aid only. Nothing here touches a file or a sidecar — it changes how a photograph is
/// drawn on screen while it is being judged, which is what "is this a black-and-white picture?" is
/// usually asked about before anything is decided.
/// </summary>
public static class Greyscale
{
    // Rec. 709 luma, the weights that match sRGB's primaries — and so the space these images are
    // decoded into and shown in. A flat average would be simpler and wrong in a way that shows:
    // it lightens blues and darkens greens, so skies come up milky and foliage goes muddy, which
    // for a landscape is the whole picture.
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <summary>
    /// Greys one row of BGRA pixels in place, leaving alpha alone.
    ///
    /// A row at a time because the caller works over a locked framebuffer a row at a time: a
    /// full-frame buffer for a 26-megapixel photograph is about a hundred megabytes, and copying
    /// one out and back again to avoid pointer arithmetic would cost more than the conversion.
    /// </summary>
    public static void GreyRowBgra(Span<byte> row)
    {
        for (var i = 0; i + 3 < row.Length; i += 4)
        {
            var grey = (byte)Math.Clamp(
                (row[i + 2] * RedWeight) + (row[i + 1] * GreenWeight) + (row[i] * BlueWeight),
                0f,
                255f);

            row[i] = grey;
            row[i + 1] = grey;
            row[i + 2] = grey;
            // row[i + 3] is alpha, and a photograph's opacity is not a colour.
        }
    }
}
