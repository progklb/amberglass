using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Carrying the application's own folder across the rename.
///
/// This folder holds everything that cannot be rebuilt from the media: settings, the keyword
/// library, the catalog and the working tree of unwritten edits. Left behind by a rename, the
/// application would open on what looks exactly like a first run — no workspace, no vocabulary, no
/// pending work — while the real folder sat beside it under the old name.
/// </summary>
public class AppDataFolderTests
{
    private static string Former(TempFolder temp) => Path.Combine(temp.Path, AppPaths.FormerFolderName);

    private static string Current(TempFolder temp) => Path.Combine(temp.Path, AppPaths.FolderName);

    [Fact]
    public void The_old_folder_is_brought_across()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Former(temp));
        File.WriteAllText(Path.Combine(Former(temp), "settings.json"), "{ \"kept\": true }");

        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));

        Assert.False(Directory.Exists(Former(temp)));
        Assert.Equal("{ \"kept\": true }", File.ReadAllText(Path.Combine(Current(temp), "settings.json")));
    }

    /// <summary>Everything under it, not just the files at the top: the catalog and the caches too.</summary>
    [Fact]
    public void Everything_underneath_comes_with_it()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(Former(temp), "Cache", "Thumbnails"));
        File.WriteAllText(Path.Combine(Former(temp), "Cache", "Thumbnails", "a.jpg"), "thumb");
        File.WriteAllText(Path.Combine(Former(temp), "pending-changes.jsonl"), "{}");

        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));

        Assert.True(File.Exists(Path.Combine(Current(temp), "Cache", "Thumbnails", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(Current(temp), "pending-changes.jsonl")));
    }

    /// <summary>
    /// A folder under the new name is this application's own, and newer. Moving the old one over it
    /// would discard a session's work to recover one that was already superseded.
    /// </summary>
    [Fact]
    public void An_existing_folder_is_never_overwritten()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Former(temp));
        File.WriteAllText(Path.Combine(Former(temp), "settings.json"), "old");
        Directory.CreateDirectory(Current(temp));
        File.WriteAllText(Path.Combine(Current(temp), "settings.json"), "current");

        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));

        Assert.Equal("current", File.ReadAllText(Path.Combine(Current(temp), "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Former(temp), "settings.json")));
    }

    /// <summary>A first run on a machine that never had the old name: nothing to do, and no folder invented.</summary>
    [Fact]
    public void A_first_run_is_left_alone()
    {
        using var temp = new TempFolder();

        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));

        Assert.False(Directory.Exists(Current(temp)));
    }

    /// <summary>Run twice — on the second launch, and on every launch after it.</summary>
    [Fact]
    public void Running_again_changes_nothing()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Former(temp));
        File.WriteAllText(Path.Combine(Former(temp), "settings.json"), "kept");

        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));
        AppPaths.AdoptFormerFolder(temp.Path, Current(temp));

        Assert.Equal("kept", File.ReadAllText(Path.Combine(Current(temp), "settings.json")));
    }
}
