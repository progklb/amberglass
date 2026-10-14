using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

public class EmbedQueueTests
{
    /// <summary>A request to embed is kept like a pending edit is: across a restart.</summary>
    [Fact]
    public void The_queue_survives_a_restart()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var first = new EmbedQueue(paths);
        first.Add("/library/a.jpg");
        first.Add("/library/b.mp4");
        first.Remove("/library/a.jpg");

        var second = new EmbedQueue(paths);

        Assert.Equal(["/library/b.mp4"], second.GetAll());
    }

    [Fact]
    public void Adding_twice_raises_one_change()
    {
        var queue = new EmbedQueue();
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        queue.Add("/library/a.jpg");
        queue.Add("/library/a.jpg");

        Assert.Equal(1, changes);
        Assert.Equal(1, queue.Count);
    }

    // ---- What the sidecar says that the file does not ------------------------------------------

    [Fact]
    public void A_rating_only_in_the_sidecar_is_what_the_file_lacks()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Rating = 0, Title = "Same" },
            Sidecar = new EditableMetadata { Rating = 4 },
            SidecarPath = "/library/a.xmp"
        };

        var diff = MetadataDiff.SidecarOverFile(metadata);

        // The title is only in the file, so the sidecar is silent on it: no difference.
        var change = Assert.Single(diff.Fields);
        Assert.Equal(new FieldChange("Rating", "Unrated", "4 stars"), change);
    }

    /// <summary>"Unrated" in the sidecar against nothing in the file is the same answer twice.</summary>
    [Fact]
    public void An_explicit_unrated_against_no_rating_is_not_a_difference()
    {
        var metadata = new MediaMetadata
        {
            Embedded = EditableMetadata.Empty,
            Sidecar = new EditableMetadata { Rating = 0 },
            SidecarPath = "/library/a.xmp"
        };

        Assert.True(MetadataDiff.SidecarOverFile(metadata).IsEmpty);
    }

    [Fact]
    public void Without_a_sidecar_nothing_is_ahead()
        => Assert.True(MetadataDiff.SidecarOverFile(new MediaMetadata { Embedded = new EditableMetadata { Rating = 3 } }).IsEmpty);

    // ---- What Sync does to a file -----------------------------------------------------------------

    private static SyncPlanItem Planned(string name, bool hasEdit = true, bool embedRequested = false) => new(
        new MediaFile
        {
            FullPath = "/library/" + name,
            FileName = name,
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        EditableMetadata.Empty,
        HasConflict: false)
    {
        HasEdit = hasEdit,
        EmbedRequested = embedRequested
    };

    private static readonly SyncOptions EmbedOff = new() { EmbedMetadata = false };
    private static readonly SyncOptions EmbedOn = new() { EmbedMetadata = true };

    [Fact]
    public void An_edit_follows_the_embed_option()
    {
        Assert.Equal(SyncAction.SidecarOnly, Planned("a.jpg").ActionFor(EmbedOff));
        Assert.Equal(SyncAction.SidecarAndFile, Planned("a.jpg").ActionFor(EmbedOn));
    }

    /// <summary>Asking for one file is the consent the global option otherwise stands in for.</summary>
    [Fact]
    public void A_requested_embed_happens_with_the_option_off()
    {
        Assert.Equal(SyncAction.SidecarAndFile, Planned("a.jpg", embedRequested: true).ActionFor(EmbedOff));
        Assert.Equal(SyncAction.FileOnly, Planned("a.jpg", hasEdit: false, embedRequested: true).ActionFor(EmbedOff));
    }

    [Fact]
    public void Camera_raw_is_never_embedded()
    {
        Assert.Equal(SyncAction.SidecarOnly, Planned("a.raf").ActionFor(EmbedOn));
        Assert.Equal(SyncAction.SidecarOnly, Planned("a.raf", embedRequested: true).ActionFor(EmbedOn));
    }
}
