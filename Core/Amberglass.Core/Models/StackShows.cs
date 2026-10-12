namespace Amberglass.Core.Models;

/// <summary>
/// Which half of a collapsed pair the grid shows.
///
/// Pinned like the other appearance enums, and for the same reason — stored as numbers.
/// </summary>
public enum StackShows
{
    /// <summary>The negative. The default: it is the file the edits belong to.</summary>
    Raw = 0,

    /// <summary>The camera's own rendering, which is quicker to draw and often quicker to judge.</summary>
    Jpeg = 1
}
