namespace Amberglass.UI.Services;

/// <summary>
/// Formats workspace paths for the Open Recent menu.
///
/// The folder name alone is ambiguous — every library has a "2024" — but a full path drags the menu
/// wider than the screen, so the name leads and an abbreviated path follows.
/// </summary>
public static class WorkspaceLabel
{
    /// <summary>Longer than this and the path is elided from the left, keeping the tail.</summary>
    private const int MaxPathLength = 45;

    public static string ForMenu(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));

        return string.IsNullOrEmpty(name)
            ? path
            : string.IsNullOrEmpty(parent) ? name : $"{name}  —  {Abbreviate(parent)}";
    }

    /// <summary>
    /// Substitutes ~ for the home directory and elides the middle of anything still too long. The
    /// tail is what identifies a folder, so the front is what gets dropped.
    /// </summary>
    internal static string Abbreviate(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shortened = !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;

        return shortened.Length <= MaxPathLength
            ? shortened
            : "…" + shortened[^MaxPathLength..];
    }
}
