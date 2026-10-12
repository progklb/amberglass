using System.Diagnostics;

namespace Amberglass.UI.Services;

/// <summary>
/// Hands a path to the platform's file manager, in one of two ways.
///
/// <see cref="Reveal"/> selects a file among its siblings; <see cref="OpenFolder"/> shows what is
/// inside a folder. Both are standard, and which one is wanted depends on what was clicked: from a
/// tile you want to find that one file on disk, from the folder tree you want to be in the folder.
/// Neither opens anything in another application — the point is to get to it, not to launch it.
/// </summary>
public static class RevealInFileManager
{
    /// <summary>Named after whatever the platform calls its file manager, so the menu reads right.</summary>
    public static string MenuHeader { get; } = OperatingSystem.IsMacOS()
        ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in File Manager";

    /// <summary>
    /// The folder tree's wording. "Open" rather than "Reveal" because it is the honest description:
    /// the folder is shown from the inside, not picked out from among its neighbours.
    /// </summary>
    public static string OpenFolderMenuHeader { get; } = OperatingSystem.IsMacOS()
        ? "Open in Finder"
        : OperatingSystem.IsWindows() ? "Open in Explorer" : "Open in File Manager";

    /// <summary>
    /// The command that selects <paramref name="path"/> in the platform's file manager. Separated
    /// from launching it so the platform quirks can be tested without starting processes.
    /// </summary>
    internal static (string Command, string[] Arguments) BuildCommand(string path, PlatformKind platform)
        => platform switch
        {
            PlatformKind.MacOS => ("open", ["-R", path]),

            // No space after the comma: explorer reads "/select, path" as two arguments and opens
            // Documents instead of selecting anything.
            PlatformKind.Windows => ("explorer.exe", [$"/select,{path}"]),

            // Linux file managers vary too much to rely on a select flag, so open the folder.
            _ => ("xdg-open", [Path.GetDirectoryName(path) ?? path])
        };

    /// <summary>
    /// The command that opens <paramref name="path"/> itself. The same three launchers as
    /// <see cref="BuildCommand"/> with the selection flags dropped, which is all "open the folder"
    /// means on any of them.
    /// </summary>
    internal static (string Command, string[] Arguments) BuildOpenCommand(string path, PlatformKind platform)
        => platform switch
        {
            PlatformKind.MacOS => ("open", [path]),
            PlatformKind.Windows => ("explorer.exe", [path]),
            _ => ("xdg-open", [path])
        };

    internal enum PlatformKind
    {
        MacOS,
        Windows,
        Other
    }

    private static PlatformKind Current => OperatingSystem.IsMacOS()
        ? PlatformKind.MacOS
        : OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Other;

    public static void Reveal(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Launch(BuildCommand(path, Current));
        }
    }

    /// <summary>
    /// Opens a folder in the file manager. Does nothing for a path that is blank — the folder tree
    /// carries placeholder nodes with no path of their own while their parent is still unexpanded.
    /// </summary>
    public static void OpenFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Launch(BuildOpenCommand(path, Current));
        }
    }

    private static void Launch((string Command, string[] Arguments) command)
    {
        var startInfo = new ProcessStartInfo { FileName = command.Command, UseShellExecute = false };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
    }
}
