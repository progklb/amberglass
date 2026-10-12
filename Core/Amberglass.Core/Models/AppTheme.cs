namespace Amberglass.Core.Models;

/// <summary>
/// Which set of surface colours the application paints itself in.
///
/// Values are pinned rather than left to declaration order, because these are written into
/// settings.json as numbers. Inserting a theme in the middle of an unpinned enum would silently
/// repaint every existing user's application in whatever now holds their saved number.
/// </summary>
public enum AppTheme
{
    /// <summary>
    /// Near-black. Named for where a photograph is judged: the application recedes and nothing
    /// competes with the picture for the eye's idea of what black is.
    /// </summary>
    Darkroom = 0,

    /// <summary>
    /// A single dark grey throughout — the tone Darkroom reserves for the grid and the preview,
    /// used for the whole application so no panel sits lighter than its neighbour.
    /// </summary>
    Graphite = 1,

    /// <summary>
    /// Deep red-black, after the safelight a darkroom is lit by — the one lamp that will not fog
    /// the paper. Dark enough to keep judging by, and unmistakably red.
    /// </summary>
    Safelight = 2,

    /// <summary>
    /// Dark teal, after the green that grows on weathered copper.
    /// </summary>
    Verdigris = 3,

    /// <summary>
    /// Deep amber-brown, after the amber safelight of a development room — the lamp the name
    /// Amberglass comes from. Warmer and softer than Safelight's red.
    /// </summary>
    Amberlight = 4
}
