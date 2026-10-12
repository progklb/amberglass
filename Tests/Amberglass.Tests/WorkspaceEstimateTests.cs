using Amberglass.Core.Interfaces;
using Xunit;

namespace Amberglass.Tests;

public class WorkspaceEstimateTests
{
    [Fact]
    public void An_empty_workspace_has_nothing_to_do()
    {
        var estimate = WorkspaceEstimate.Empty;

        Assert.True(estimate.IsEmpty);
        Assert.Equal(0, estimate.Files);
        Assert.Equal(0, estimate.ImageBytes);
    }

    /// <summary>
    /// The renditions dominate. A thousand JPEGs cost thumbnails; a thousand RAWs cost thumbnails
    /// plus a full rendition each, which is more than forty times as much disk — the difference the
    /// dialog exists to show before anyone starts.
    /// </summary>
    [Fact]
    public void Raw_files_dominate_the_disk_estimate()
    {
        var jpegs = new WorkspaceEstimate(Images: 1000, RawImages: 0, Videos: 0, VideoBytes: 0);
        var raws = new WorkspaceEstimate(Images: 1000, RawImages: 1000, Videos: 0, VideoBytes: 0);

        Assert.Equal(1000 * WorkspaceEstimate.ThumbnailBytes, jpegs.ImageBytes);
        Assert.Equal(
            (1000 * WorkspaceEstimate.RenditionBytes) + (1000 * WorkspaceEstimate.ThumbnailBytes),
            raws.ImageBytes);

        Assert.True(raws.ImageBytes > jpegs.ImageBytes * 40);
    }

    /// <summary>Only the RAWs carry the develop cost; the rest is thumbnail work.</summary>
    [Fact]
    public void Only_raw_files_carry_the_develop_time()
    {
        var mixed = new WorkspaceEstimate(Images: 100, RawImages: 40, Videos: 0, VideoBytes: 0);

        var expected = ((40 * WorkspaceEstimate.RawDevelopSeconds) + (100 * WorkspaceEstimate.ThumbnailSeconds)) / 4;

        Assert.Equal(expected, mixed.EstimateImageTime(4).TotalSeconds, 6);
    }

    [Fact]
    public void More_lanes_means_less_waiting()
    {
        var estimate = new WorkspaceEstimate(Images: 500, RawImages: 500, Videos: 0, VideoBytes: 0);

        Assert.Equal(
            estimate.EstimateImageTime(1).TotalSeconds / 4,
            estimate.EstimateImageTime(4).TotalSeconds,
            6);
    }

    /// <summary>A nonsense lane count must not divide by zero or produce a negative wait.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_nonsense_lane_count_falls_back_to_one(int lanes)
    {
        var estimate = new WorkspaceEstimate(Images: 10, RawImages: 10, Videos: 0, VideoBytes: 0);

        Assert.Equal(estimate.EstimateImageTime(1), estimate.EstimateImageTime(lanes));
    }

    /// <summary>
    /// The real library, so the figures in the dialog can be sanity-checked against something known:
    /// 2,235 RAWs, 650 JPEGs, 491 clips.
    /// </summary>
    [Fact]
    public void The_reference_library_lands_where_it_was_measured()
    {
        var estimate = new WorkspaceEstimate(Images: 2885, RawImages: 2235, Videos: 491, VideoBytes: 100_000_000_000);

        // ~15 GB of renditions, which is what the render cache work measured.
        Assert.InRange(estimate.ImageBytes / 1_000_000_000.0, 14, 17);

        // Hours, not minutes — the thing worth knowing before starting.
        Assert.InRange(estimate.EstimateImageTime(4).TotalMinutes, 30, 60);
    }

    [Fact]
    public void Video_is_estimated_from_the_source_size()
    {
        var estimate = new WorkspaceEstimate(Images: 0, RawImages: 0, Videos: 10, VideoBytes: 1_000_000_000);

        Assert.Equal((long)(1_000_000_000 * WorkspaceEstimate.ProxyBytesPerSourceByte), estimate.ProxyBytes);
    }

    [Fact]
    public void Files_counts_images_and_videos()
        => Assert.Equal(15, new WorkspaceEstimate(10, 4, 5, 0).Files);
}
