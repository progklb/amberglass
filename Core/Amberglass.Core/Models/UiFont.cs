namespace Amberglass.Core.Models;

/// <summary>
/// Which typeface the interface is set in.
///
/// The faces here are bundled rather than taken from the system, so the choice means the same thing
/// on every platform. Pinned like the other appearance enums — these are stored as numbers.
/// </summary>
public enum UiFont
{
    /// <summary>Whatever the platform uses. The default, and unchanged from before the setting.</summary>
    System = 0,

    /// <summary>
    /// Andika. Drawn by SIL for literacy teaching, which is why it can be used for everything
    /// including filenames and exposure values: it is friendly without being a handwriting face,
    /// and its digits and letterforms are deliberately unambiguous.
    /// </summary>
    Andika = 1,

    /// <summary>
    /// Delius. Genuinely handwritten and still even. Warmer than Andika and rather less formal, at
    /// some cost to clarity on long strings of digits.
    /// </summary>
    Delius = 2
}
