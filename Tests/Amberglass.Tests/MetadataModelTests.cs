using System.Collections.Immutable;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

public class EditableMetadataTests
{
    [Fact]
    public void Merge_lets_the_overlay_win_where_it_has_values()
    {
        var embedded = new EditableMetadata { Title = "Embedded", Rating = 2, Creator = "Kevin" };
        var sidecar = new EditableMetadata { Title = "Sidecar", Rating = 5 };

        var merged = embedded.MergeWith(sidecar);

        Assert.Equal("Sidecar", merged.Title);
        Assert.Equal(5, merged.Rating);
    }

    [Fact]
    public void Merge_keeps_base_values_the_overlay_is_silent_about()
    {
        var embedded = new EditableMetadata { Title = "Embedded", Creator = "Kevin" };
        var sidecar = new EditableMetadata { Rating = 5 };

        var merged = embedded.MergeWith(sidecar);

        // A sidecar carrying only a rating must not blank out the embedded title.
        Assert.Equal("Embedded", merged.Title);
        Assert.Equal("Kevin", merged.Creator);
        Assert.Equal(5, merged.Rating);
    }

    [Fact]
    public void Merge_with_null_overlay_is_a_no_op()
    {
        var embedded = new EditableMetadata { Title = "Embedded" };

        Assert.Same(embedded, embedded.MergeWith(null));
    }

    [Fact]
    public void Merge_replaces_keywords_wholesale_rather_than_appending()
    {
        var embedded = new EditableMetadata { Keywords = ["a", "b"] };
        var sidecar = new EditableMetadata { Keywords = ["c"] };

        Assert.Equal(["c"], embedded.MergeWith(sidecar).Keywords.ToArray());
    }

    [Fact]
    public void Merge_keeps_base_keywords_when_the_overlay_has_none()
    {
        var embedded = new EditableMetadata { Keywords = ["a", "b"] };
        var sidecar = new EditableMetadata { Rating = 1 };

        Assert.Equal(["a", "b"], embedded.MergeWith(sidecar).Keywords.ToArray());
    }

    [Fact]
    public void Value_equality_compares_keywords_by_content()
    {
        var first = new EditableMetadata { Title = "T", Keywords = ["a", "b"] };
        var second = new EditableMetadata { Title = "T", Keywords = ["a", "b"] };

        // Record equality would compare the ImmutableArray by reference and report false here.
        Assert.True(first.ValueEquals(second));
    }

    [Fact]
    public void Value_equality_notices_keyword_differences()
    {
        var first = new EditableMetadata { Keywords = ["a", "b"] };
        var second = new EditableMetadata { Keywords = ["a", "c"] };
        var third = new EditableMetadata { Keywords = ["b", "a"] };

        Assert.False(first.ValueEquals(second));
        Assert.False(first.ValueEquals(third));
    }
}

public class MediaMetadataTests
{
    [Fact]
    public void Effective_prefers_the_sidecar()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Rating = 1, Title = "Embedded" },
            Sidecar = new EditableMetadata { Rating = 5 },
            SidecarPath = "/library/IMG001.xmp"
        };

        Assert.True(metadata.HasSidecar);
        Assert.Equal(5, metadata.Effective.Rating);
        Assert.Equal("Embedded", metadata.Effective.Title);
    }

    [Fact]
    public void Effective_is_the_embedded_layer_when_there_is_no_sidecar()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Rating = 3 } };

        Assert.False(metadata.HasSidecar);
        Assert.Equal(3, metadata.Effective.Rating);
    }
}

public class XmpSidecarTests
{
    [Fact]
    public void Prefers_the_adobe_extension_replacing_convention()
        => Assert.Equal(
            Path.Combine("lib", "IMG001.xmp"),
            XmpSidecar.GetPreferredPath(Path.Combine("lib", "IMG001.CR3")));

    [Fact]
    public void Finds_an_adobe_style_sidecar()
    {
        using var temp = new TempFolder();
        var media = temp.CreateFile("IMG001.CR3");
        var sidecar = temp.CreateFile("IMG001.xmp");

        Assert.Equal(sidecar, XmpSidecar.Find(media));
    }

    [Fact]
    public void Finds_an_appended_style_sidecar()
    {
        using var temp = new TempFolder();
        var media = temp.CreateFile("VID001.MP4");
        var sidecar = temp.CreateFile("VID001.MP4.xmp");

        Assert.Equal(sidecar, XmpSidecar.Find(media));
    }

    [Fact]
    public void Returns_null_when_there_is_no_sidecar()
    {
        using var temp = new TempFolder();
        var media = temp.CreateFile("IMG002.jpg");

        Assert.Null(XmpSidecar.Find(media));
    }

    [Fact]
    public void An_xmp_file_does_not_find_itself()
    {
        using var temp = new TempFolder();
        var xmp = temp.CreateFile("IMG003.xmp");

        Assert.Null(XmpSidecar.Find(xmp));
    }
}

public class PendingChangeStoreTests
{
    private static readonly EditableMetadata Original = new() { Title = "Original", Rating = 2 };

    [Fact]
    public void Records_an_edit()
    {
        var store = new PendingChangeStore();
        store.Set("/a.jpg", Original, Original with { Title = "Edited" });

        Assert.True(store.HasChanges("/a.jpg"));
        Assert.Equal(1, store.Count);
        Assert.Equal("Edited", store.GetEdited("/a.jpg")!.Title);
    }

    [Fact]
    public void Editing_back_to_the_original_clears_the_pending_change()
    {
        var store = new PendingChangeStore();
        store.Set("/a.jpg", Original, Original with { Title = "Edited" });
        store.Set("/a.jpg", Original, Original with { Title = "Original" });

        Assert.False(store.HasChanges("/a.jpg"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Identical_keyword_lists_do_not_count_as_a_change()
    {
        var store = new PendingChangeStore();
        var original = new EditableMetadata { Keywords = ["a", "b"] };
        var rebuilt = new EditableMetadata { Keywords = ImmutableArray.Create("a", "b") };

        store.Set("/a.jpg", original, rebuilt);

        Assert.False(store.HasChanges("/a.jpg"));
    }

    [Fact]
    public void Discard_removes_a_single_entry()
    {
        var store = new PendingChangeStore();
        store.Set("/a.jpg", Original, Original with { Title = "A" });
        store.Set("/b.jpg", Original, Original with { Title = "B" });

        store.Discard("/a.jpg");

        Assert.False(store.HasChanges("/a.jpg"));
        Assert.True(store.HasChanges("/b.jpg"));
    }

    [Fact]
    public void Discard_all_empties_the_store()
    {
        var store = new PendingChangeStore();
        store.Set("/a.jpg", Original, Original with { Title = "A" });
        store.Set("/b.jpg", Original, Original with { Title = "B" });

        store.DiscardAll();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Raises_changed_when_entries_are_added_and_removed()
    {
        var store = new PendingChangeStore();
        var events = 0;
        store.Changed += (_, _) => events++;

        store.Set("/a.jpg", Original, Original with { Title = "A" });
        store.Discard("/a.jpg");

        Assert.Equal(2, events);
    }

    [Fact]
    public void Discarding_something_absent_raises_nothing()
    {
        var store = new PendingChangeStore();
        var events = 0;
        store.Changed += (_, _) => events++;

        store.Discard("/missing.jpg");

        Assert.Equal(0, events);
    }
}
