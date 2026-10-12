using System.Runtime.CompilerServices;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

public sealed class MediaScanner : IMediaScanner
{
    private readonly ILogger<MediaScanner> _logger;

    public MediaScanner(ILogger<MediaScanner> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<MediaFile> ScanAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Scan requested for missing folder {Folder}", rootPath);
            yield break;
        }

        var filesFound = 0;
        var foldersVisited = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            foldersVisited++;
            progress?.Report(new ScanProgress(filesFound, foldersVisited, folder));

            var entries = await Task.Run(() => ReadFolder(folder, options), cancellationToken).ConfigureAwait(false);

            if (options.Recursive)
            {
                foreach (var subfolder in entries.Subfolders)
                {
                    pending.Push(subfolder);
                }
            }

            foreach (var file in entries.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesFound++;
                yield return file;
            }

            progress?.Report(new ScanProgress(filesFound, foldersVisited, folder));
        }

        progress?.Report(new ScanProgress(filesFound, foldersVisited, null));
    }

    private FolderEntries ReadFolder(string folder, ScanOptions options)
    {
        var files = new List<MediaFile>();
        var subfolders = new List<string>();

        try
        {
            var directory = new DirectoryInfo(folder);

            foreach (var info in directory.EnumerateFiles())
            {
                if (!options.IncludeHiddenFiles && IsHidden(info))
                {
                    continue;
                }

                if (!MediaTypeRegistry.IsSupported(info.FullName))
                {
                    continue;
                }

                files.Add(MediaFile.FromFileInfo(info));
            }

            foreach (var sub in directory.EnumerateDirectories())
            {
                if (!options.IncludeHiddenFiles && IsHidden(sub))
                {
                    continue;
                }

                subfolders.Add(sub.FullName);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Skipping unreadable folder {Folder}", folder);
        }

        files.Sort(static (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        return new FolderEntries(files, subfolders);
    }

    private static bool IsHidden(FileSystemInfo info)
        => info.Attributes.HasFlag(FileAttributes.Hidden) || info.Name.StartsWith('.');

    private readonly record struct FolderEntries(List<MediaFile> Files, List<string> Subfolders);
}
