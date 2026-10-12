using Amberglass.UI.Controls;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The hand-drawn mark's state machine, which is the part of it that is not a matter of taste.
///
/// <c>Animates</c> is off throughout: with it on, a redraw sets Progress to 0 and waits on a
/// dispatcher there is none of in a test. Off, a redraw snaps Progress to 1 — which makes "did it
/// redraw" observable without a UI thread.
/// </summary>
public class RoughMarkTests
{
    private static RoughMark Mark(RoughMarkKind kind = RoughMarkKind.Ring)
        => new() { Animates = false, Kind = kind };

    /// <summary>
    /// The reported fault: the pointer moving in and out of a selected row rubbed the mark out and
    /// drew it again, though what is drawn never changed. Progress is left mid-draw and must still
    /// be there afterwards.
    /// </summary>
    [Fact]
    public void HoveringSomethingAlreadySelectedDoesNotRedrawIt()
    {
        var mark = Mark();
        mark.IsSelected = true;

        mark.Progress = 0.5;

        mark.IsHovered = true;
        Assert.Equal(0.5, mark.Progress);

        mark.IsHovered = false;
        Assert.Equal(0.5, mark.Progress);
    }

    /// <summary>The same for a tile, whose selected mark is a box and whose hover is an underline.</summary>
    [Fact]
    public void HoveringASelectedTileDoesNotRedrawIt()
    {
        var mark = Mark(RoughMarkKind.Box);
        mark.IsSelected = true;

        mark.Progress = 0.25;
        mark.IsHovered = true;

        Assert.Equal(0.25, mark.Progress);
    }

    /// <summary>
    /// Guarding against redrawing must not stop a genuine change from drawing. Selecting something
    /// that was merely hovered swaps an underline for a ring, and that has to be drawn.
    /// </summary>
    [Fact]
    public void SelectingSomethingHoveredDrawsTheStrongerMark()
    {
        var mark = Mark();
        mark.IsHovered = true;

        mark.Progress = 0.5;
        mark.IsSelected = true;

        Assert.Equal(1, mark.Progress);
    }

    [Fact]
    public void HoveringSomethingUnselectedDrawsTheUnderline()
    {
        var mark = Mark();

        mark.Progress = 0.5;
        mark.IsHovered = true;

        Assert.Equal(1, mark.Progress);
    }

    /// <summary>
    /// A tab keeps its own hover, so nothing at all happens when the pointer crosses one. Without
    /// this, HoverKind None would still count as a change away from the selected mark.
    /// </summary>
    [Fact]
    public void AMarkThatIgnoresHoverIsUnmovedByIt()
    {
        var mark = new RoughMark
        {
            Animates = false,
            Kind = RoughMarkKind.Underline,
            HoverKind = RoughMarkKind.None
        };

        mark.IsSelected = true;
        mark.Progress = 0.5;

        mark.IsHovered = true;
        Assert.Equal(0.5, mark.Progress);

        mark.IsHovered = false;
        Assert.Equal(0.5, mark.Progress);
    }
}
