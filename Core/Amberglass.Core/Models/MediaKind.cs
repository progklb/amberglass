namespace Amberglass.Core.Models;

/// <summary>
/// What a file is, for filtering — a finer distinction than <see cref="MediaType"/>, which only
/// knows image from video.
///
/// RAW and JPEG are the same <see cref="MediaType"/> and completely different things to a
/// photographer: one is a negative and the other is a print. The catalog stores no flag for it, so
/// the distinction is drawn from the extension, exactly as the rest of the application draws it.
/// </summary>
public enum MediaKind
{
    /// <summary>A camera raw file.</summary>
    Raw,

    /// <summary>
    /// An image that is not raw. Named for the common case, but it covers PNG and HEIC too — the
    /// test is "not raw" rather than "is a JPEG".
    /// </summary>
    Jpeg,

    Video
}
