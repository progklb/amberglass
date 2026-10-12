namespace Amberglass.Core.Models;

/// <summary>
/// How a selected folder is marked out.
///
/// Pinned like the other appearance enums, and for the same reason — stored as numbers.
/// </summary>
public enum SelectionStyle
{
    /// <summary>A filled row, as every other application does it. The default, and not experimental.</summary>
    Standard = 0,

    /// <summary>
    /// A ring drawn round the folder name as if by pencil, in the manner of Ellipsus's menu.
    /// Opt-in and marked experimental: it suits short names and grows eccentric on long ones, which
    /// is a matter of taste rather than a defect to be fixed.
    /// </summary>
    HandDrawn = 1
}
