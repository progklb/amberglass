using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class MediaScannerTests
{
    private static MediaScanner CreateScanner() => new(NullLogger<MediaScanner>.Instance);

    private static async Task<List<MediaFile>> CollectAsync(
        IMediaScanner scanner,
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null)
    {
        var results = new List<MediaFile>();
        await foreach (var file in scanner.ScanAsync(root, options, progress))
        {
            results.Add(file);
        }

        return results;
    }

    [Fact]
    public async Task Finds_supported_media_and_ignores_other_files()
    {
        using var temp = new TempFolder();
        temp.CreateFile("a.jpg");
        temp.CreateFile("b.MP4");
        temp.CreateFile("notes.txt");
        temp.CreateFile("archive.zip");

        var files = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions());

        Assert.Equal(["a.jpg", "b.MP4"], files.Select(f => f.FileName).Order());
    }

    [Fact]
    public async Task Recurses_into_subfolders_when_requested()
    {
        using var temp = new TempFolder();
        temp.CreateFile("top.jpg");
        temp.CreateFile(Path.Combine("nested", "deep", "inner.cr3"));

        var recursive = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions { Recursive = true });
        var flat = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions { Recursive = false });

        Assert.Equal(2, recursive.Count);
        Assert.Single(flat);
        Assert.Equal("top.jpg", flat[0].FileName);
    }

    [Fact]
    public async Task Skips_hidden_files_by_default()
    {
        using var temp = new TempFolder();
        temp.CreateFile("visible.jpg");
        temp.CreateFile(".hidden.jpg");

        var withoutHidden = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions());
        var withHidden = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions { IncludeHiddenFiles = true });

        Assert.Single(withoutHidden);
        Assert.Equal(2, withHidden.Count);
    }

    [Fact]
    public async Task Classifies_media_type_from_extension()
    {
        using var temp = new TempFolder();
        temp.CreateFile("photo.ARW");
        temp.CreateFile("clip.mov");

        var files = await CollectAsync(CreateScanner(), temp.Path, new ScanOptions());

        Assert.Equal(MediaType.Image, files.Single(f => f.FileName == "photo.ARW").MediaType);
        Assert.Equal(MediaType.Video, files.Single(f => f.FileName == "clip.mov").MediaType);
    }

    [Fact]
    public async Task Reports_progress_while_scanning()
    {
        using var temp = new TempFolder();
        temp.CreateFile("a.jpg");
        temp.CreateFile(Path.Combine("sub", "b.jpg"));

        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(reports.Add);

        await CollectAsync(CreateScanner(), temp.Path, new ScanOptions(), progress);

        // Progress<T> marshals through the sync context, so give the posted callbacks a moment.
        await Task.Delay(100);

        Assert.NotEmpty(reports);
        Assert.Equal(2, reports.Max(r => r.FilesFound));
        Assert.Equal(2, reports.Max(r => r.FoldersVisited));
    }

    [Fact]
    public async Task Missing_folder_yields_nothing()
    {
        var files = await CollectAsync(
            CreateScanner(),
            Path.Combine(Path.GetTempPath(), "amberglass-does-not-exist-" + Guid.NewGuid().ToString("N")),
            new ScanOptions());

        Assert.Empty(files);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 50; i++)
        {
            temp.CreateFile($"file{i}.jpg");
        }

        using var cts = new CancellationTokenSource();
        var scanner = CreateScanner();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scanner.ScanAsync(temp.Path, new ScanOptions(), cancellationToken: cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.True(seen < 50);
    }
}
