using System.Security.Cryptography;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Metadata.Xmp;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

public class SyncPlanTests
{
    private static SyncPlanItem Item(string path, bool conflict = false) => new(
        new MediaFile
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        EditableMetadata.Empty,
        conflict);

    [Fact]
    public void Breaks_the_selection_down_by_file_type()
    {
        var plan = new SyncPlan(
            [Item("/a.jpg"), Item("/b.jpg"), Item("/c.mp4"), Item("/d.CR3"), Item("/e.jpg")],
            []);

        // The README's "45 JPG / 120 MP4 / 13 CR3" summary, most numerous first.
        Assert.Equal([("JPG", 3), ("CR3", 1), ("MP4", 1)], plan.ByExtension.ToArray());
    }

    [Fact]
    public void Counts_conflicts()
    {
        var plan = new SyncPlan([Item("/a.jpg"), Item("/b.jpg", conflict: true)], []);

        Assert.Equal(1, plan.ConflictCount);
    }

    [Fact]
    public void Knows_when_it_is_resuming()
    {
        Assert.False(new SyncPlan([Item("/a.jpg")], []).IsResuming);
        Assert.True(new SyncPlan([Item("/a.jpg")], ["/b.jpg"]).IsResuming);
    }
}

public class SyncJournalTests
{
    [Fact]
    public void An_absent_journal_reports_nothing_completed()
    {
        using var temp = new TempFolder();

        Assert.Empty(new SyncJournal(new TestPaths(temp.Path), NullLogger.Instance).LoadCompleted());
    }

    [Fact]
    public void Completed_files_survive_being_reloaded()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var journal = new SyncJournal(paths, NullLogger.Instance);
        journal.RecordCompleted("/a.jpg");
        journal.RecordCompleted("/b.jpg");

        // A fresh instance stands in for the next launch after a crash.
        Assert.Equal(["/a.jpg", "/b.jpg"], new SyncJournal(paths, NullLogger.Instance).LoadCompleted().ToArray());
    }

    [Fact]
    public void Duplicate_entries_are_collapsed()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var journal = new SyncJournal(paths, NullLogger.Instance);
        journal.RecordCompleted("/a.jpg");
        journal.RecordCompleted("/a.jpg");

        Assert.Single(journal.LoadCompleted());
    }

    [Fact]
    public void Clearing_forgets_everything()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        var journal = new SyncJournal(paths, NullLogger.Instance);

        journal.RecordCompleted("/a.jpg");
        journal.Clear();

        Assert.Empty(journal.LoadCompleted());
    }

    [Fact]
    public void The_journal_lives_outside_the_cache()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        new SyncJournal(paths, NullLogger.Instance).RecordCompleted("/a.jpg");

        // Clearing the cache must not lose a record of work already committed.
        Assert.True(File.Exists(Path.Combine(paths.AppDataRoot, "sync-journal.txt")));
        Assert.False(File.Exists(Path.Combine(paths.CacheRoot, "sync-journal.txt")));
    }
}

/// <summary>
/// Integration tests against real ExifTool. Sync is the only thing in the application that can
/// modify a user's original media, so its promises are verified against real files.
/// </summary>
public class SyncServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Temp = new TempFolder();
            Paths = new TestPaths(Temp.Path);
            Host = new ExifToolHost(new RealExifTool.Locator(), NullLogger<ExifToolHost>.Instance);
            Reader = new ExifToolMetadataProvider(Host, NullLogger<ExifToolMetadataProvider>.Instance);
            Writer = new ExifToolSidecarWriter(Host, Reader, NullLogger<ExifToolSidecarWriter>.Instance);
            Store = new PendingChangeStore();
            Service = new SyncService(Store, Reader, Writer, Paths, NullLogger<SyncService>.Instance);
        }

        public TempFolder Temp { get; }

        public TestPaths Paths { get; }

        public ExifToolHost Host { get; }

        public ExifToolMetadataProvider Reader { get; }

        public ExifToolSidecarWriter Writer { get; }

        public PendingChangeStore Store { get; }

        public SyncService Service { get; }

        public MediaFile CreateJpeg(string name)
        {
            var path = Path.Combine(Temp.Path, name);

            using (var bitmap = new SKBitmap(64, 48))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.SlateGray);
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            }

            return MediaFile.FromFileInfo(new FileInfo(path));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temp.Dispose();
        }
    }

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Sidecar_only_sync_leaves_the_original_untouched()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG001.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Title = "Synced", Rating = 4 });

        var before = HashOf(file.FullPath);
        var modifiedBefore = File.GetLastWriteTimeUtc(file.FullPath);

        var options = new SyncOptions { EmbedMetadata = false };
        var plan = await fixture.Service.PrepareAsync(options);
        var result = await fixture.Service.ExecuteAsync(plan, options);

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.Failures);

        // The default path must remain completely non-destructive.
        Assert.Equal(before, HashOf(file.FullPath));
        Assert.Equal(modifiedBefore, File.GetLastWriteTimeUtc(file.FullPath));
        Assert.True(File.Exists(Path.ChangeExtension(file.FullPath, ".xmp")));
    }

    [Fact]
    public async Task Embedding_writes_into_the_original_and_keeps_a_backup()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG002.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty,
            new EditableMetadata { Title = "Embedded title", Rating = 5, Keywords = ["wildlife"] });

        var before = HashOf(file.FullPath);

        var options = new SyncOptions { EmbedMetadata = true, BackupOriginals = true };
        var plan = await fixture.Service.PrepareAsync(options);
        var result = await fixture.Service.ExecuteAsync(plan, options);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(SyncOutcome.Embedded, result.Items[0].Outcome);

        // The original really is modified this time...
        Assert.NotEqual(before, HashOf(file.FullPath));

        // ...and the backup holds the bytes it used to have.
        var backup = file.FullPath + "_original";
        Assert.True(File.Exists(backup));
        Assert.Equal(before, HashOf(backup));

        var reread = await fixture.Reader.ReadAsync(MediaFile.FromFileInfo(new FileInfo(file.FullPath)));
        Assert.Equal("Embedded title", reread!.Embedded.Title);
        Assert.Equal(5, reread.Embedded.Rating);
        Assert.Equal(["wildlife"], reread.Embedded.Keywords.ToArray());
    }

    [Fact]
    public async Task Embedding_without_backups_leaves_no_original_copy()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG003.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Title = "No backup" });

        var options = new SyncOptions { EmbedMetadata = true, BackupOriginals = false };
        var plan = await fixture.Service.PrepareAsync(options);
        await fixture.Service.ExecuteAsync(plan, options);

        Assert.False(File.Exists(file.FullPath + "_original"));
    }

    [Fact]
    public async Task Embedding_can_preserve_the_file_timestamp()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG004.jpg");

        // Backdated so a rewritten timestamp would be unmistakable.
        var original = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file.FullPath, original);

        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Title = "Timestamped" });

        var options = new SyncOptions { EmbedMetadata = true, PreserveTimestamps = true, BackupOriginals = false };
        var plan = await fixture.Service.PrepareAsync(options);
        await fixture.Service.ExecuteAsync(plan, options);

        // Bridge changing timestamps on every keyword edit is the complaint this project began with.
        Assert.Equal(original, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public async Task A_synced_file_is_no_longer_pending()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG005.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 3 });

        var options = new SyncOptions();
        var plan = await fixture.Service.PrepareAsync(options);
        await fixture.Service.ExecuteAsync(plan, options);

        Assert.Equal(0, fixture.Store.Count);
    }

    /// <summary>
    /// The sidecar is the record for a proprietary raw file, and nothing writes into the file. Asking
    /// Sync to embed must not make an exception of that.
    /// </summary>
    [Fact]
    public async Task A_proprietary_raw_file_is_never_embedded_into()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();

        // A JPEG named as a CR2 is enough: the decision is made on the extension, and this keeps the
        // test from needing a real raw file to prove the file is left untouched.
        var raw = fixture.CreateJpeg("IMG100.cr2");
        var before = await File.ReadAllBytesAsync(raw.FullPath);

        fixture.Store.Set(raw.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 4 });

        var options = new SyncOptions { EmbedMetadata = true };
        var plan = await fixture.Service.PrepareAsync(options);
        var result = await fixture.Service.ExecuteAsync(plan, options);

        var item = Assert.Single(result.Items);
        Assert.Equal(SyncOutcome.SidecarWritten, item.Outcome);

        // The record went beside it, and the file itself is byte for byte what it was.
        Assert.True(File.Exists(Path.ChangeExtension(raw.FullPath, ".xmp")));
        Assert.Equal(before, await File.ReadAllBytesAsync(raw.FullPath));
    }

    [Fact]
    public async Task Conflicted_files_are_skipped_when_asked()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG006.jpg");

        // The sidecar first, then the file: a conflict is the file having changed *after* the
        // sidecar, which is what says somebody else has been here. The other order is this
        // application's own last word and settles nothing.
        await fixture.Writer.WriteSidecarAsync(
            MediaFile.FromFileInfo(new FileInfo(file.FullPath)),
            new EditableMetadata { Title = "From the sidecar" },
            new SidecarWriteOptions { ValidateAfterWrite = false });

        await fixture.Writer.WriteEmbeddedAsync(file, new EditableMetadata { Title = "From the file" },
            new EmbedWriteOptions { BackupOriginal = false, ValidateAfterWrite = false });

        // This application preserves the file's timestamp when it embeds, so its own writes never
        // look like somebody else's. An outside editor does not, and that is what is being staged
        // here: the file touched after the sidecar was last written.
        File.SetLastWriteTimeUtc(file.FullPath, DateTime.UtcNow.AddMinutes(1));

        var current = MediaFile.FromFileInfo(new FileInfo(file.FullPath));
        fixture.Store.Set(current.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 2 });

        var options = new SyncOptions { SkipConflicted = true };
        var plan = await fixture.Service.PrepareAsync(options);

        Assert.Equal(1, plan.ConflictCount);

        var result = await fixture.Service.ExecuteAsync(plan, options);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Succeeded);

        // A skipped file keeps its pending change, so nothing is silently lost.
        Assert.True(fixture.Store.HasChanges(current.FullPath));
    }

    [Fact]
    public async Task An_interrupted_run_resumes_rather_than_repeating_itself()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var first = fixture.CreateJpeg("R1.jpg");
        var second = fixture.CreateJpeg("R2.jpg");

        foreach (var file in new[] { first, second })
        {
            fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 4 });
        }

        // Simulate a run that committed the first file and then died.
        var journal = new SyncJournal(fixture.Paths, NullLogger.Instance);
        journal.RecordCompleted(first.FullPath);

        var plan = await fixture.Service.PrepareAsync(new SyncOptions());

        Assert.True(plan.IsResuming);
        Assert.Equal(1, plan.Count);
        Assert.Equal(second.FullPath, plan.Items[0].File.FullPath);
    }

    [Fact]
    public async Task Discarding_resume_state_makes_the_next_run_start_over()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("R3.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 1 });

        new SyncJournal(fixture.Paths, NullLogger.Instance).RecordCompleted(file.FullPath);
        Assert.Equal(0, (await fixture.Service.PrepareAsync(new SyncOptions())).Count);

        fixture.Service.DiscardResumeState();

        Assert.Equal(1, (await fixture.Service.PrepareAsync(new SyncOptions())).Count);
    }

    [Fact]
    public async Task A_successful_run_clears_the_journal()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("R4.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 2 });

        var options = new SyncOptions();
        await fixture.Service.ExecuteAsync(await fixture.Service.PrepareAsync(options), options);

        // Otherwise the next unrelated sync would wrongly think it was resuming.
        Assert.Empty(new SyncJournal(fixture.Paths, NullLogger.Instance).LoadCompleted());
    }

    [Fact]
    public async Task Nothing_pending_means_nothing_to_do()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();

        var plan = await fixture.Service.PrepareAsync(new SyncOptions());

        Assert.Equal(0, plan.Count);
    }

    [Fact]
    public async Task Embedding_and_the_sidecar_end_up_agreeing()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        await using var fixture = new Fixture();
        var file = fixture.CreateJpeg("IMG007.jpg");
        fixture.Store.Set(file.FullPath, EditableMetadata.Empty,
            new EditableMetadata { Title = "Agreed", Rating = 3 });

        var options = new SyncOptions { EmbedMetadata = true, BackupOriginals = false };
        await fixture.Service.ExecuteAsync(await fixture.Service.PrepareAsync(options), options);

        var reread = await fixture.Reader.ReadAsync(MediaFile.FromFileInfo(new FileInfo(file.FullPath)));

        // Sync writes both layers, so a synced file must not immediately look conflicted.
        Assert.NotNull(reread!.Sidecar);
        Assert.Equal("Agreed", reread.Embedded.Title);
        Assert.Equal("Agreed", reread.Sidecar.Title);
        Assert.Empty(MetadataConflictDetector.Detect(reread));
    }
}
