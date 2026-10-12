using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

public sealed class FolderBrowser : IFolderBrowser
{
    private readonly ILogger<FolderBrowser> _logger;

    public FolderBrowser(ILogger<FolderBrowser> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<FolderNode> GetRoots()
    {
        var roots = new List<FolderNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home) && seen.Add(home))
        {
            roots.Add(new FolderNode(home, "Home"));
        }

        foreach (var node in GetVolumes())
        {
            if (seen.Add(node.FullPath))
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    /// <summary>
    /// Unix reports dozens of pseudo-filesystems through <see cref="DriveInfo"/> (/dev,
    /// /System/Volumes/..., and so on). Listing mount points directly keeps the tree to volumes a
    /// person would actually browse.
    /// </summary>
    private IEnumerable<FolderNode> GetVolumes()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FolderNode(d.RootDirectory.FullName, d.Name));
        }

        var volumes = new List<FolderNode> { new("/", "/") };

        var mountRoots = OperatingSystem.IsMacOS()
            ? new[] { "/Volumes" }
            : new[] { "/media", "/mnt", Path.Combine("/run/media", Environment.UserName) };

        foreach (var mountRoot in mountRoots)
        {
            volumes.AddRange(GetSubfolders(mountRoot));
        }

        return volumes;
    }

    public IReadOnlyList<FolderNode> GetSubfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FolderNode(d.FullName, d.Name))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Unable to list subfolders of {Folder}", path);
            return [];
        }
    }
}
