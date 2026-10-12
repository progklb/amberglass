using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class IncrementalIndexingTests
{
    /// <summary>Counts how many files it is asked to read, which is the cost being avoided.</summary>
    private sealed class CountingMetadataProvider : IMetadataProvider
    {
        public int FilesRead { get; private set; }

        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaMetadata?>(MediaMetadata.Empty);

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FilesRead += files.Count;
            return Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(
                files.ToDictionary(f => f.FullPath, _ => MediaMetadata.Empty));
        }
    }

    private static MediaFile File(string path, long size = 100, long modified = 1_700_000_000)
        => new()
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            MediaType = MediaType.Image,
            SizeBytes = size,
            ModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(modified),
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

    private static (CatalogIndexer Indexer, CountingMetadataProvider Metadata, SqliteCatalog Catalog) Create(TempFolder temp)
    {
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);
        var metadata = new CountingMetadataProvider();
        return (new CatalogIndexer(metadata, catalog, NullLogger<CatalogIndexer>.Instance), metadata, catalog);
    }

    [Fact]
    public void An_unknown_file_needs_indexing()
        => Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg"), new Dictionary<string, IndexedStamp>(), 0));

    [Fact]
    public void An_unchanged_file_does_not()
    {
        var known = new Dictionary<string, IndexedStamp> { ["/a.jpg"] = new(100, 1_700_000_000, CatalogIndexer.CurrentVersion) };

        Assert.False(CatalogIndexer.NeedsIndexing(File("/a.jpg"), known, 0));
    }

    [Fact]
    public void A_file_whose_size_changed_needs_reindexing()
    {
        var known = new Dictionary<string, IndexedStamp> { ["/a.jpg"] = new(100, 1_700_000_000, CatalogIndexer.CurrentVersion) };

        Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg", size: 101), known, 0));
    }

    [Fact]
    public void A_file_whose_timestamp_changed_needs_reindexing()
    {
        var known = new Dictionary<string, IndexedStamp> { ["/a.jpg"] = new(100, 1_700_000_000, CatalogIndexer.CurrentVersion) };

        // Same size, edited in place — the timestamp is the only signal.
        Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg", modified: 1_700_000_001), known, 0));
    }

    [Fact]
    public async Task Stamps_come_back_for_known_paths_only()
    {
        using var temp = new TempFolder();
        var (indexer, _, catalog) = Create(temp);

        await indexer.IndexAsync([File("/a.jpg", size: 42, modified: 123)]);

        var stamps = await catalog.GetIndexedStampsAsync(["/a.jpg", "/never-seen.jpg"]);

        Assert.Equal(new IndexedStamp(42, 123, CatalogIndexer.CurrentVersion), stamps["/a.jpg"]);
        Assert.False(stamps.ContainsKey("/never-seen.jpg"));
    }

    [Fact]
    public async Task Asking_for_nothing_queries_nothing()
    {
        using var temp = new TempFolder();
        var (_, _, catalog) = Create(temp);

        // An empty IN clause is a SQL syntax error, so this must short-circuit.
        Assert.Empty(await catalog.GetIndexedStampsAsync([]));
    }

    [Fact]
    public async Task Reindexing_an_unchanged_workspace_reads_no_files()
    {
        using var temp = new TempFolder();
        var (indexer, metadata, _) = Create(temp);

        var files = Enumerable.Range(0, 250).Select(i => File($"/w/{i}.jpg")).ToList();

        var first = await indexer.IndexAsync(files);
        Assert.Equal(250, first.Indexed);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(250, metadata.FilesRead);

        var second = await indexer.IndexAsync(files);

        // The whole point: reopening a workspace must not re-read anything.
        Assert.Equal(0, second.Indexed);
        Assert.Equal(250, second.Skipped);
        Assert.Equal(250, metadata.FilesRead);
    }

    [Fact]
    public async Task Only_the_changed_files_are_reread()
    {
        using var temp = new TempFolder();
        var (indexer, metadata, _) = Create(temp);

        var files = Enumerable.Range(0, 10).Select(i => File($"/w/{i}.jpg")).ToList();
        await indexer.IndexAsync(files);

        var changed = files.ToList();
        changed[3] = File("/w/3.jpg", size: 999);

        var result = await indexer.IndexAsync(changed);

        Assert.Equal(1, result.Indexed);
        Assert.Equal(9, result.Skipped);
        Assert.Equal(11, metadata.FilesRead);
    }

    [Fact]
    public async Task An_interrupted_index_keeps_what_it_finished()
    {
        using var temp = new TempFolder();
        var (indexer, _, catalog) = Create(temp);

        var files = Enumerable.Range(0, 500).Select(i => File($"/w/{i}.jpg")).ToList();

        // Cancel once the first chunks have been committed.
        using var cts = new CancellationTokenSource();
        var progress = new Progress<JobProgress>(p =>
        {
            if (p.Completed >= 200)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => indexer.IndexAsync(files, progress, cts.Token));

        var stats = await catalog.GetStatisticsAsync();

        // Work done before the interruption survives, which is why quitting mid-index is cheap.
        Assert.InRange(stats.FileCount, 100, 500);
    }

    [Fact]
    public async Task Resuming_after_an_interruption_only_reads_the_remainder()
    {
        using var temp = new TempFolder();
        var (indexer, metadata, catalog) = Create(temp);

        var files = Enumerable.Range(0, 400).Select(i => File($"/w/{i}.jpg")).ToList();

        using var cts = new CancellationTokenSource();
        var progress = new Progress<JobProgress>(p =>
        {
            if (p.Completed >= 200)
            {
                cts.Cancel();
            }
        });

        try
        {
            await indexer.IndexAsync(files, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var readBeforeResume = metadata.FilesRead;
        var alreadyIn = (await catalog.GetStatisticsAsync()).FileCount;

        var resumed = await indexer.IndexAsync(files);

        Assert.Equal(alreadyIn, resumed.Skipped);
        Assert.Equal(400 - alreadyIn, resumed.Indexed);
        Assert.Equal(readBeforeResume + resumed.Indexed, metadata.FilesRead);
    }
}

public class IndexingChoiceTests
{
    [Fact]
    public void A_choice_is_remembered_per_workspace()
    {
        var settings = AppSettings.Default
            .WithIndexingChoice("/a", true)
            .WithIndexingChoice("/b", false);

        Assert.True(settings.WorkspaceIndexing["/a"]);
        Assert.False(settings.WorkspaceIndexing["/b"]);
    }

    [Fact]
    public void A_choice_can_be_changed()
    {
        var settings = AppSettings.Default
            .WithIndexingChoice("/a", false)
            .WithIndexingChoice("/a", true);

        Assert.True(settings.WorkspaceIndexing["/a"]);
        Assert.Single(settings.WorkspaceIndexing);
    }

    [Fact]
    public void An_unanswered_workspace_has_no_entry()
        => Assert.False(AppSettings.Default.WorkspaceIndexing.ContainsKey("/never-opened"));
}

public class PruneIntegrityTests
{
    private static CatalogEntry Entry(string path, params string[] keywords)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = MediaType.Image,
                SizeBytes = 10,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata { Title = "Kept", Keywords = [.. keywords] },
            CameraInfo.Empty,
            HasSidecar: false,
            null);

    [Fact]
    public async Task Pruning_leaves_no_orphaned_keyword_links()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        var present = Path.Combine(temp.Path, "present.jpg");
        await File.WriteAllTextAsync(present, "x");

        await catalog.UpsertAsync([
            Entry(present, "kept"),
            Entry(Path.Combine(temp.Path, "gone.jpg"), "dropped", "shared")
        ]);

        // one link for present.jpg, two for gone.jpg
        Assert.Equal(3, await catalog.CountKeywordLinksAsync());

        await catalog.RemoveMissingAsync();

        // The keyword links of a removed file must go with it. They hang off a foreign key with
        // ON DELETE CASCADE, which SQLite ignores unless foreign_keys is on for the connection
        // doing the delete — off by default, and a per-connection setting.
        Assert.Equal(1, await catalog.CountKeywordLinksAsync());
    }
}

/// <summary>
/// Staleness has a second cause besides the file changing, and it is the one that bit: a row written
/// by an older indexer is out of date even though the file it describes is untouched.
/// </summary>
public class IndexerVersionTests
{
    private static MediaFile File(string path, long size = 100, long modified = 1_700_000_000) => new()
    {
        FullPath = path,
        FileName = System.IO.Path.GetFileName(path),
        MediaType = MediaType.Image,
        SizeBytes = size,
        ModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(modified),
        CreatedUtc = DateTimeOffset.FromUnixTimeSeconds(modified)
    };

    /// <summary>
    /// The bug this exists to prevent: adding the cull flag left every existing row with a null
    /// flag, and because no file had changed, nothing ever re-read them. A search for rejected
    /// photographs answered "none" on a workspace full of them.
    /// </summary>
    [Fact]
    public void A_row_from_an_older_indexer_is_stale_even_though_the_file_is_untouched()
    {
        var known = new Dictionary<string, IndexedStamp>
        {
            ["/a.jpg"] = new(100, 1_700_000_000, CatalogIndexer.CurrentVersion - 1)
        };

        Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg"), known, 0));
    }

    [Fact]
    public void A_row_from_this_indexer_is_current()
    {
        var known = new Dictionary<string, IndexedStamp>
        {
            ["/a.jpg"] = new(100, 1_700_000_000, CatalogIndexer.CurrentVersion)
        };

        Assert.False(CatalogIndexer.NeedsIndexing(File("/a.jpg"), known, 0));
    }

    /// <summary>
    /// A catalog written before the column existed defaults to 0, which must not read as current —
    /// that default is precisely the "indexed by something older" case.
    /// </summary>
    [Fact]
    public void The_default_version_is_never_current()
    {
        Assert.NotEqual(0, CatalogIndexer.CurrentVersion);
    }
}

/// <summary>
/// Noticing that a sidecar changed.
///
/// This is the gap that let a rejected photograph sit in the catalog unflagged: ratings, labels and
/// flags are written to the XMP sidecar, which leaves the media file's size and modified time exactly
/// as they were. Judged on the media file alone, nothing had changed and nothing was re-read.
/// </summary>
public class SidecarStalenessTests
{
    private static MediaFile File(string path)
        => new()
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            MediaType = MediaType.Image,
            SizeBytes = 100,
            ModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

    private static Dictionary<string, IndexedStamp> Known(string path, long sidecar)
        => new()
        {
            [path] = new IndexedStamp(100, 1_700_000_000, CatalogIndexer.CurrentVersion, sidecar)
        };

    [Fact]
    public void AnUnchangedSidecarIsNotStale()
        => Assert.False(CatalogIndexer.NeedsIndexing(File("/a.jpg"), Known("/a.jpg", 1_700_000_500), 1_700_000_500));

    [Fact]
    public void ASidecarWrittenSinceIsStale()
    {
        // The media file is untouched — same size, same modified time. Only the sidecar moved.
        Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg"), Known("/a.jpg", 1_700_000_500), 1_700_000_900));
    }

    [Fact]
    public void GainingASidecarIsStale()
        => Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg"), Known("/a.jpg", 0), 1_700_000_900));

    /// <summary>Deleting one changes the metadata just as much as writing one.</summary>
    [Fact]
    public void LosingASidecarIsStale()
        => Assert.True(CatalogIndexer.NeedsIndexing(File("/a.jpg"), Known("/a.jpg", 1_700_000_500), 0));

    [Fact]
    public void AFileThatNeverHadOneIsNotStale()
        => Assert.False(CatalogIndexer.NeedsIndexing(File("/a.jpg"), Known("/a.jpg", 0), 0));
}

/// <summary>
/// The whole loop: a sidecar edited outside this application, and the next index picking it up.
/// </summary>
public class SidecarReindexingTests
{
    private sealed class CountingProvider : IMetadataProvider
    {
        public int FilesRead { get; private set; }

        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaMetadata?>(MediaMetadata.Empty);

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FilesRead += files.Count;
            return Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(
                files.ToDictionary(f => f.FullPath, _ => MediaMetadata.Empty));
        }
    }

    [Fact]
    public async Task EditingASidecarMakesTheFileBeReadAgain()
    {
        using var temp = new TempFolder();

        var media = Path.Combine(temp.Path, "a.raf");
        var sidecar = Path.Combine(temp.Path, "a.xmp");
        System.IO.File.WriteAllText(media, "raw");
        System.IO.File.WriteAllText(sidecar, "<xmp>3 stars</xmp>");

        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);
        var provider = new CountingProvider();
        var indexer = new CatalogIndexer(provider, catalog, NullLogger<CatalogIndexer>.Instance);

        var files = (MediaFile[])[MediaFile.FromFileInfo(new FileInfo(media))];

        await indexer.IndexAsync(files);
        Assert.Equal(1, provider.FilesRead);

        // Nothing has changed, so nothing is read.
        await indexer.IndexAsync(files);
        Assert.Equal(1, provider.FilesRead);

        // Somebody rates it in Lightroom. The raw file is untouched — same bytes, same timestamp —
        // and before this was fixed that made the change invisible to the catalog.
        System.IO.File.WriteAllText(sidecar, "<xmp>5 stars</xmp>");
        System.IO.File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddMinutes(5));

        await indexer.IndexAsync(files);
        Assert.Equal(2, provider.FilesRead);
    }

    [Fact]
    public async Task AFileWithNoSidecarIsStillOnlyReadOnce()
    {
        // The cost of the fix has to fall on files that have a sidecar, not on every file.
        using var temp = new TempFolder();

        var media = Path.Combine(temp.Path, "a.raf");
        System.IO.File.WriteAllText(media, "raw");

        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);
        var provider = new CountingProvider();
        var indexer = new CatalogIndexer(provider, catalog, NullLogger<CatalogIndexer>.Instance);

        var files = (MediaFile[])[MediaFile.FromFileInfo(new FileInfo(media))];

        await indexer.IndexAsync(files);
        await indexer.IndexAsync(files);
        await indexer.IndexAsync(files);

        Assert.Equal(1, provider.FilesRead);
    }
}

/// <summary>
/// Finding the sidecar, and reading when it was written, on real files.
/// </summary>
public class SidecarTimestampTests
{
    [Fact]
    public void NoSidecarReadsAsZero()
    {
        using var temp = new TempFolder();
        var media = Path.Combine(temp.Path, "a.raf");
        System.IO.File.WriteAllText(media, "raw");

        Assert.Equal(0, XmpSidecar.LastWrittenUtc(media));
    }

    [Theory]
    [InlineData("a.xmp")]      // Adobe's convention: the extension is replaced.
    [InlineData("a.raf.xmp")]  // The other one in the wild: appended to the whole name.
    public void EitherConventionIsFound(string sidecarName)
    {
        using var temp = new TempFolder();
        var media = Path.Combine(temp.Path, "a.raf");
        System.IO.File.WriteAllText(media, "raw");
        System.IO.File.WriteAllText(Path.Combine(temp.Path, sidecarName), "<xmp/>");

        Assert.NotEqual(0, XmpSidecar.LastWrittenUtc(media));
    }

    [Fact]
    public void RewritingTheSidecarMovesTheStamp()
    {
        using var temp = new TempFolder();
        var media = Path.Combine(temp.Path, "a.raf");
        var sidecar = Path.Combine(temp.Path, "a.xmp");
        System.IO.File.WriteAllText(media, "raw");
        System.IO.File.WriteAllText(sidecar, "<xmp/>");

        var before = XmpSidecar.LastWrittenUtc(media);

        // Set explicitly rather than by writing again: the stamp has one-second resolution, and a
        // test that waited a second to prove it would be a test that takes a second.
        System.IO.File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddMinutes(5));

        Assert.NotEqual(before, XmpSidecar.LastWrittenUtc(media));
    }

    /// <summary>An .xmp file is not its own sidecar, which would make every one of them stale forever.</summary>
    [Fact]
    public void AnXmpFileDoesNotFindItself()
    {
        using var temp = new TempFolder();
        var sidecar = Path.Combine(temp.Path, "a.xmp");
        System.IO.File.WriteAllText(sidecar, "<xmp/>");

        Assert.Equal(0, XmpSidecar.LastWrittenUtc(sidecar));
    }
}
