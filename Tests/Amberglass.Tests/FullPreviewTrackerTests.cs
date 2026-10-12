using Amberglass.UI.ViewModels;
using Xunit;

namespace Amberglass.Tests;

public class FullPreviewTrackerTests
{
    private const string FileA = "/library/A.RAF";
    private const string FileB = "/library/B.RAF";

    // ---- The regression -------------------------------------------------------------------------

    /// <summary>
    /// The reported bug. A run that ended without producing anything used to leave the request
    /// recorded, and every later request for that file was then dismissed as redundant — so opening a
    /// RAW showed its embedded JPEG and never developed, until pressing \ happened to clear the flag.
    /// </summary>
    [Fact]
    public void A_run_that_delivers_nothing_does_not_block_the_next_attempt()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Ended(FileA); // cancelled, or the selection moved, or the decode failed

        Assert.True(tracker.ShouldStart(FileA));
    }

    [Fact]
    public void A_run_that_delivered_blocks_the_next_attempt()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Delivering(FileA);
        tracker.Ended(FileA);

        Assert.False(tracker.ShouldStart(FileA));
    }

    // ---- De-duplication, which is what the guard is for ------------------------------------------

    /// <summary>
    /// Selecting an item raises several properties, each asking the viewer to refresh. Without this
    /// the same develop would be started and cancelled two or three times.
    /// </summary>
    [Fact]
    public void Repeat_requests_while_a_decode_is_running_are_ignored()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);

        Assert.False(tracker.ShouldStart(FileA));
        Assert.False(tracker.ShouldStart(FileA));
    }

    [Fact]
    public void A_different_file_starts_even_while_one_is_running()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);

        Assert.True(tracker.ShouldStart(FileB));
    }

    /// <summary>
    /// An older run finishing late must not clear the marker belonging to the one that replaced it,
    /// or the newer decode would be started a second time.
    /// </summary>
    [Fact]
    public void A_late_finish_does_not_disturb_the_run_that_replaced_it()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Begin(FileB);
        tracker.Ended(FileA);

        Assert.False(tracker.ShouldStart(FileB));
    }

    // ---- Holding pixels across a re-render -------------------------------------------------------

    /// <summary>
    /// Re-developing the same photograph is a comparison. The pixels in hand must survive it, or the
    /// viewer drops to a lower-quality rendition for the several seconds that make the comparison
    /// worth doing.
    /// </summary>
    [Fact]
    public void Changing_the_rendering_keeps_the_pixels_but_allows_a_new_decode()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Delivering(FileA);
        tracker.Ended(FileA);

        tracker.Invalidate();

        Assert.True(tracker.ShouldStart(FileA));
        Assert.False(tracker.IsChangingFile(FileA));
    }

    /// <summary>A genuinely different photograph must have the old pixels thrown away first.</summary>
    [Fact]
    public void Moving_to_another_file_counts_as_changing_file()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Delivering(FileA);

        Assert.True(tracker.IsChangingFile(FileB));
    }

    [Fact]
    public void Nothing_held_counts_as_changing_file()
        => Assert.True(new FullPreviewTracker().IsChangingFile(FileA));

    // ---- Releasing -------------------------------------------------------------------------------

    /// <summary>
    /// Closing the viewer releases the bitmap. The loupe in the main window wants the same file, so a
    /// later request has to decode it again rather than being told it is already done.
    /// </summary>
    [Fact]
    public void Forgetting_the_pixels_allows_a_new_decode()
    {
        var tracker = new FullPreviewTracker();

        tracker.Begin(FileA);
        tracker.Delivering(FileA);
        tracker.Ended(FileA);

        tracker.Forget();

        Assert.True(tracker.ShouldStart(FileA));
        Assert.Null(tracker.Held);
    }

    /// <summary>The whole sequence a single selection goes through, in order.</summary>
    [Fact]
    public void A_normal_selection_decodes_once_and_only_once()
    {
        var tracker = new FullPreviewTracker();

        Assert.True(tracker.ShouldStart(FileA));
        tracker.Begin(FileA);

        // The refresh storm that follows a selection.
        Assert.False(tracker.ShouldStart(FileA));
        Assert.False(tracker.ShouldStart(FileA));

        tracker.Delivering(FileA);
        tracker.Ended(FileA);

        // And afterwards, when the viewer refreshes again for its own reasons.
        Assert.False(tracker.ShouldStart(FileA));
    }
}
