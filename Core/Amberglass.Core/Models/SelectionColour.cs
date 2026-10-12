namespace Amberglass.Core.Models;

/// <summary>
/// Where the colour of a selected thumbnail or folder comes from.
///
/// Kept apart from <see cref="AppTheme"/> because the two are genuinely separate preferences: the
/// same person may want the application dark and quiet but the selection loud enough to find at a
/// glance across a full grid, or the exact opposite.
///
/// Pinned like <see cref="AppTheme"/>, and for the same reason — these are stored as numbers.
/// </summary>
public enum SelectionColour
{
    /// <summary>
    /// The colour the operating system is set to highlight with, which Avalonia already resolves
    /// from the platform — so this tracks a change made in System Settings without restarting.
    /// This is what the application did before the setting existed, and so remains the default.
    /// </summary>
    System = 0,

    /// <summary>
    /// A colour belonging to the theme, so the selection sits in the same family as everything
    /// around it rather than being the one thing on screen that is not.
    /// </summary>
    Theme = 1
}
