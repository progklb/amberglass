using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using Xunit;

namespace BetterDAM.Tests;

public class MetadataConflictDetectorTests
{
    private static MediaMetadata WithLayers(EditableMetadata embedded, EditableMetadata? sidecar)
        => new()
        {
            Embedded = embedded,
            Sidecar = sidecar,
            SidecarPath = sidecar is null ? null : "/library/IMG001.xmp"
        };

    [Fact]
    public void No_sidecar_means_no_conflicts()
    {
        var metadata = WithLayers(new EditableMetadata { Title = "Embedded" }, null);

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void A_field_the_sidecar_does_not_mention_is_not_a_conflict()
    {
        // The common case: a sidecar carrying only a rating must not be reported as conflicting
        // with every field the media file happens to have.
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded", Creator = "Kevin" },
            new EditableMetadata { Rating = 5 });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Identical_values_are_not_a_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Same", Rating = 3 },
            new EditableMetadata { Title = "Same", Rating = 3 });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Differing_values_on_both_sides_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5 });

        var conflicts = MetadataConflictDetector.Detect(metadata);

        Assert.Equal(2, conflicts.Length);

        var title = conflicts.Single(c => c.Field == MetadataConflictDetector.TitleField);
        Assert.Equal("Embedded title", title.EmbeddedValue);
        Assert.Equal("Sidecar title", title.SidecarValue);

        var rating = conflicts.Single(c => c.Field == MetadataConflictDetector.RatingField);
        Assert.Equal("1", rating.EmbeddedValue);
        Assert.Equal("5", rating.SidecarValue);
    }

    [Fact]
    public void Keyword_order_alone_is_not_a_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"] },
            new EditableMetadata { Keywords = ["b", "a"] });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Different_keyword_membership_conflicts()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"] },
            new EditableMetadata { Keywords = ["a", "c"] });

        var conflict = Assert.Single(MetadataConflictDetector.Detect(metadata));
        Assert.Equal(MetadataConflictDetector.KeywordsField, conflict.Field);
    }

    [Fact]
    public void Keep_embedded_takes_the_embedded_value_but_keeps_sidecar_only_fields()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5, Label = "Red" });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.KeepEmbedded);

        Assert.Equal("Embedded title", resolved.Title);
        Assert.Equal(1, resolved.Rating);

        // The sidecar was the only source of a label; resolving a conflict must not discard it.
        Assert.Equal("Red", resolved.Label);
    }

    [Fact]
    public void Keep_sidecar_takes_the_sidecar_value()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5 });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.KeepSidecar);

        Assert.Equal("Sidecar title", resolved.Title);
        Assert.Equal(5, resolved.Rating);
    }

    [Fact]
    public void Merge_unions_the_keywords()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"], Title = "Embedded" },
            new EditableMetadata { Keywords = ["b", "c"], Title = "Sidecar" });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.Merge);

        Assert.Equal(["a", "b", "c"], resolved.Keywords.OrderBy(k => k).ToArray());

        // Single-valued fields cannot be merged, so the sidecar wins.
        Assert.Equal("Sidecar", resolved.Title);
    }

    // ---- What is not a conflict ------------------------------------------------------------------

    private static MediaMetadata Stamped(
        EditableMetadata embedded, EditableMetadata sidecar, int fileMinute, int sidecarMinute)
        => WithLayers(embedded, sidecar) with
        {
            FileMetadataChangedUtc = new DateTimeOffset(2026, 1, 1, 12, fileMinute, 0, TimeSpan.Zero),
            SidecarMetadataChangedUtc = new DateTimeOffset(2026, 1, 1, 12, sidecarMinute, 0, TimeSpan.Zero)
        };

    /// <summary>
    /// Every camera measured — Fujifilm RAF, Canon CR2, their JPEGs, Lightroom's DNG — writes
    /// Rating 0 into every file. Reading that as a value made the first star given to any file
    /// disagree with the file it was given to.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void A_rating_of_zero_means_unrated_and_disagrees_with_nothing(int rating)
    {
        var metadata = WithLayers(
            new EditableMetadata { Rating = 0 },
            new EditableMetadata { Rating = rating });

        Assert.Empty(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
    }

    [Fact]
    public void Two_real_ratings_that_differ_are_still_a_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Rating = 2 },
            new EditableMetadata { Rating = 5 });

        var conflict = Assert.Single(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
        Assert.Equal(MetadataConflictDetector.RatingField, conflict.Field);
    }

    /// <summary>
    /// Nothing writes into a CR2 or an NEF, so the file keeping what the camera gave it while the
    /// sidecar carries the photographer's answer is the arrangement working.
    /// </summary>
    [Theory]
    [InlineData("/library/IMG001.cr2")]
    [InlineData("/library/IMG001.nef")]
    [InlineData("/library/IMG001.raf")]
    [InlineData("/library/IMG001.arw")]
    public void A_proprietary_raw_file_never_conflicts_with_its_sidecar(string path)
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "From the camera" },
            new EditableMetadata { Title = "From the photographer" });

        Assert.Empty(MetadataConflictDetector.Detect(metadata, path));
    }

    /// <summary>
    /// DNG is raw but is a container built to hold XMP, and Adobe writes into it. Exempting it would
    /// have quietly stopped stitched panoramas from ever syncing.
    /// </summary>
    [Theory]
    [InlineData("/library/IMG001.dng")]
    [InlineData("/library/IMG001.jpg")]
    [InlineData("/library/IMG001.tif")]
    [InlineData("/library/CLIP.mov")]
    public void A_format_we_write_into_still_conflicts(string path)
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "In the file" },
            new EditableMetadata { Title = "In the sidecar" });

        Assert.Single(MetadataConflictDetector.Detect(metadata, path));
    }

    /// <summary>
    /// The ordinary state of every file this application has edited: the sidecar is its last word.
    /// </summary>
    [Fact]
    public void A_sidecar_newer_than_its_file_is_an_override_rather_than_a_disagreement()
    {
        var metadata = Stamped(
            new EditableMetadata { Title = "In the file" },
            new EditableMetadata { Title = "In the sidecar" },
            fileMinute: 0, sidecarMinute: 5);

        Assert.Empty(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
    }

    [Fact]
    public void A_file_changed_after_its_sidecar_is_somebody_else_and_is_worth_asking_about()
    {
        var metadata = Stamped(
            new EditableMetadata { Title = "In the file" },
            new EditableMetadata { Title = "In the sidecar" },
            fileMinute: 5, sidecarMinute: 0);

        Assert.Single(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
    }

    /// <summary>
    /// Written in the same second, which is all the resolution a preserved timestamp keeps. Equal is
    /// not "after", so it stays quiet rather than asking a question nobody can answer.
    /// </summary>
    [Fact]
    public void Identical_timestamps_are_not_treated_as_the_file_having_changed()
    {
        var metadata = Stamped(
            new EditableMetadata { Title = "In the file" },
            new EditableMetadata { Title = "In the sidecar" },
            fileMinute: 3, sidecarMinute: 3);

        Assert.Empty(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
    }

    /// <summary>
    /// A missing timestamp must not silence a real disagreement: the cost of asking is a question,
    /// the cost of staying quiet is a lost edit.
    /// </summary>
    [Fact]
    public void An_unknown_timestamp_leaves_the_disagreement_standing()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "In the file" },
            new EditableMetadata { Title = "In the sidecar" });

        Assert.Single(MetadataConflictDetector.Detect(metadata, "/library/IMG001.jpg"));
    }
}
