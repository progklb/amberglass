using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.UI.Converters;
using Amberglass.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>The Sync window's per-file actions, the stack note in the inspector header, and the rating stars.</summary>
public class SyncWindowTests
{
    private sealed class NoopThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file, int maxEdgePixels, ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    /// <summary>Hands back a fixed plan; the window is what is under test.</summary>
    private sealed class PlannedSync(SyncPlan plan) : ISyncService
    {
        public Task<SyncPlan> PrepareAsync(SyncOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(plan);

        public Task<SyncResult> ExecuteAsync(SyncPlan plan, SyncOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SyncResult([], false));

        public void DiscardResumeState()
        {
        }
    }

    private static MediaFile FileAt(string path) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static readonly MediaFile Edited = FileAt("/ws/a.jpg");
    private static readonly MediaFile Queued = FileAt("/ws/b.jpg");

    private static async Task<(SyncViewModel Sync, PendingChangeStore Pending, EmbedQueue Queue)> OpenAsync()
    {
        var pending = new PendingChangeStore();
        pending.Set(Edited.FullPath, EditableMetadata.Empty, new EditableMetadata { Rating = 3 });
        var queue = new EmbedQueue();
        queue.Add(Queued.FullPath);

        var plan = new SyncPlan(
        [
            new SyncPlanItem(Edited, new EditableMetadata { Rating = 3 }, false),
            new SyncPlanItem(Queued, new EditableMetadata { Rating = 4 }, false) { HasEdit = false, EmbedRequested = true }
        ], []);

        var sync = new SyncViewModel(new PlannedSync(plan), new NoopThumbnails(), pending, queue, NullLogger<SyncViewModel>.Instance);
        await sync.PrepareAsync();
        return (sync, pending, queue);
    }

    [Fact]
    public async Task Discarding_an_edit_forgets_it_and_takes_it_out_of_the_run()
    {
        var (sync, pending, _) = await OpenAsync();

        sync.DropFileCommand.Execute(sync.Files[0]);

        Assert.False(pending.HasChanges(Edited.FullPath));
        Assert.Equal([Queued.FullPath], sync.Files.Select(f => f.Plan.File.FullPath));
        Assert.Equal(1, sync.FileCount);
    }

    [Fact]
    public async Task Not_embedding_a_queued_file_takes_it_off_the_queue()
    {
        var (sync, _, queue) = await OpenAsync();
        var file = sync.Files.Single(f => !f.Plan.HasEdit);
        Assert.Equal("Don't embed", file.DropLabel);

        sync.DropFileCommand.Execute(file);

        Assert.False(queue.Contains(Queued.FullPath));
        Assert.Single(sync.Files);
    }

    /// <summary>Sync is modal, so going to a file means closing it and saying which file.</summary>
    [Fact]
    public async Task Show_in_grid_closes_with_the_file_to_select()
    {
        var (sync, _, _) = await OpenAsync();
        var closed = false;
        sync.CloseRequested += () => closed = true;

        sync.ShowInGridCommand.Execute(sync.Files[1]);

        Assert.True(closed);
        Assert.Equal(Queued.FullPath, sync.RevealPath);
    }

    // ---- Stack note -------------------------------------------------------------------------------

    /// <summary>RAW and JPEG of one frame read one sidecar, so an edit to either shows on both.</summary>
    [Fact]
    public void A_stack_says_its_files_share_one_sidecar()
    {
        using var temp = new TempFolder();
        var raw = temp.CreateFile("DSCF7054.RAF");
        var jpeg = temp.CreateFile("DSCF7054.JPG");

        var face = new MediaItemViewModel(MediaFile.FromFileInfo(new FileInfo(raw)), new NoopThumbnails())
        {
            Stacked = [MediaFile.FromFileInfo(new FileInfo(jpeg))]
        };

        Assert.Equal("Saved to DSCF7054.xmp, shared with DSCF7054.JPG", face.StackNote);
    }

    /// <summary>A JPEG with a sidecar of its own (the appended naming) does not share the RAW's.</summary>
    [Fact]
    public void A_stack_member_with_its_own_sidecar_is_not_named()
    {
        using var temp = new TempFolder();
        var raw = temp.CreateFile("DSCF7055.RAF");
        var jpeg = temp.CreateFile("DSCF7055.JPG");
        temp.CreateFile("DSCF7055.JPG.xmp");

        var face = new MediaItemViewModel(MediaFile.FromFileInfo(new FileInfo(raw)), new NoopThumbnails())
        {
            Stacked = [MediaFile.FromFileInfo(new FileInfo(jpeg))]
        };

        Assert.Equal("Saved to DSCF7055.xmp, for this file only", face.StackNote);
    }

    [Fact]
    public void A_single_file_has_no_stack_note()
        => Assert.Null(new MediaItemViewModel(Edited, new NoopThumbnails()).StackNote);

    // ---- Rating stars -----------------------------------------------------------------------------

    [Theory]
    [InlineData(3, "3", true)]
    [InlineData(3, "4", false)]
    [InlineData(null, "1", false)]
    [InlineData(0, "1", false)]
    public void A_star_is_filled_up_to_the_rating(int? rating, string position, bool filled)
        => Assert.Equal(filled, new StarFilledConverter().Convert(rating, typeof(bool), position, System.Globalization.CultureInfo.InvariantCulture));
}
