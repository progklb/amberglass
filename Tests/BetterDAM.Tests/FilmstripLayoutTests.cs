using BetterDAM.UI.Controls;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// The filmstrip lays every tile out in one row that scrolls sideways. What makes that affordable
/// on a folder of thousands is realizing only the span in view, so these pin that span.
/// </summary>
public class FilmstripLayoutTests
{
    private static (int First, int Last) At(double x, int itemCount = 4000, double width = 200, double itemWidth = 160)
        => VirtualizingWrapPanel.VisibleColumns(itemCount, x, x + width, width, itemWidth);

    [Fact]
    public void AtTheStartItRealizesFromTheFirstItem()
    {
        var (first, last) = At(0);

        Assert.Equal(0, first);

        // A 200px viewport over 160px tiles shows two, plus a tile of buffer on each side.
        Assert.InRange(last, 1, 4);
    }

    [Fact]
    public void ScrolledAlongItRealizesOnlyWhatIsThere()
    {
        var (first, last) = At(160 * 1000);

        Assert.InRange(first, 998, 1000);
        Assert.InRange(last, 1000, 1004);

        // The point of the whole exercise: a folder of 4,000 costs a handful of tiles, not 4,000.
        Assert.True(last - first < 10, $"realized {last - first + 1} tiles");
    }

    [Fact]
    public void TheWindowSizeDoesNotGrowWithTheFolder()
    {
        var small = At(160 * 50, itemCount: 100);
        var huge = At(160 * 50, itemCount: 500_000);

        Assert.Equal(small.Last - small.First, huge.Last - huge.First);
    }

    [Fact]
    public void ItNeverRunsPastTheEnd()
    {
        var (first, last) = At(160 * 5000, itemCount: 20);

        Assert.Equal(19, first);
        Assert.Equal(19, last);
    }

    [Fact]
    public void AnEmptyStripRealizesNothing()
    {
        var (first, last) = At(0, itemCount: 0);

        Assert.True(last < first);
    }

    /// <summary>Before the first viewport report, enough to not look broken.</summary>
    [Fact]
    public void WithNoViewportYetItStillRealizesSomething()
    {
        var (first, last) = VirtualizingWrapPanel.VisibleColumns(4000, 0, 0, 0, 160);

        Assert.Equal(0, first);
        Assert.InRange(last, 0, 4);
    }
}
