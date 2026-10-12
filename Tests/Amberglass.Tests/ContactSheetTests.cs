using System.Text.Json;
using Avalonia;
using Amberglass.Core.Models;
using Amberglass.UI.Controls;
using Amberglass.UI.Converters;
using Amberglass.UI.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The contact sheet experiment: the parts of it that are rules rather than taste.
/// </summary>
public class ContactSheetTests
{
    [Fact]
    public void IsOffUnlessAskedFor()
    {
        Assert.False(AppSettings.Default.ContactSheet);
    }

    [Fact]
    public void SurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { ContactSheet = true };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.True(restored.ContactSheet);
    }

    // ---- The cross ------------------------------------------------------------------------------

    private static readonly Rect Frame = new(0, 0, 120, 120);

    /// <summary>Corner to corner, and past the corners: a grease pencil does not stop at the frame.</summary>
    [Fact]
    public void CrossRunsCornerToCornerAndPastThem()
    {
        var (first, second) = RoughGeometry.Cross(Frame, seed: 1, roughness: 1.0);

        Assert.True(first[0].X < Frame.Left && first[0].Y < Frame.Top);
        Assert.True(first[^1].X > Frame.Right && first[^1].Y > Frame.Bottom);

        Assert.True(second[0].X > Frame.Right && second[0].Y < Frame.Top);
        Assert.True(second[^1].X < Frame.Left && second[^1].Y > Frame.Bottom);
    }

    /// <summary>
    /// Both ends of each stroke land exactly on the diagonal, because the wobble tapers to nothing
    /// there. It is what keeps the cross aimed at the corners however rough the hand.
    /// </summary>
    [Fact]
    public void CrossStrokesStartAndEndOnTheDiagonal()
    {
        var (first, second) = RoughGeometry.Cross(Frame, seed: 9, roughness: 2.4);

        // On a square the falling diagonal is x == y and the rising one is x + y == side.
        Assert.Equal(first[0].X, first[0].Y, precision: 6);
        Assert.Equal(first[^1].X, first[^1].Y, precision: 6);

        Assert.Equal(Frame.Width, second[0].X + second[0].Y, precision: 6);
        Assert.Equal(Frame.Width, second[^1].X + second[^1].Y, precision: 6);
    }

    /// <summary>With no roughness the pencil is a ruler: every point sits on the diagonal.</summary>
    [Fact]
    public void CrossWithNoRoughnessIsStraight()
    {
        var (first, _) = RoughGeometry.Cross(Frame, seed: 3, roughness: 0);

        Assert.All(first, point => Assert.Equal(point.X, point.Y, precision: 6));
    }

    /// <summary>
    /// Seeded, so a frame draws the same cross every time it is painted. Unseeded noise would
    /// have the mark crawl with every scroll — the fault every pencil here is built to avoid.
    /// </summary>
    [Fact]
    public void CrossIsTheSameForTheSameSeed()
    {
        var a = RoughGeometry.Cross(Frame, seed: 42, roughness: 1.0);
        var b = RoughGeometry.Cross(Frame, seed: 42, roughness: 1.0);
        var other = RoughGeometry.Cross(Frame, seed: 43, roughness: 1.0);

        Assert.Equal(a.First, b.First);
        Assert.Equal(a.Second, b.Second);
        Assert.NotEqual(a.First, other.First);
    }

    /// <summary>The two strokes are the two diagonals, not the same stroke twice.</summary>
    [Fact]
    public void CrossStrokesAreDifferentDiagonals()
    {
        var (first, second) = RoughGeometry.Cross(Frame, seed: 5, roughness: 1.0);

        Assert.NotEqual(first, second);
        Assert.True(first[0].X < second[0].X);
    }

    /// <summary>The verdict marks are told to ignore hover; a cross must honour that like a ring does.</summary>
    [Fact]
    public void ACrossThatIgnoresHoverIsUnmovedByIt()
    {
        var mark = new RoughMark
        {
            Animates = false,
            Kind = RoughMarkKind.Cross,
            HoverKind = RoughMarkKind.None
        };

        mark.IsSelected = true;
        mark.Progress = 0.5;

        mark.IsHovered = true;
        Assert.Equal(0.5, mark.Progress);
    }

    // ---- The badge ------------------------------------------------------------------------------

    /// <summary>
    /// The badge shows for a rating whatever else is true, and for a flag only while the flag is
    /// its to show. The case that matters is the last row: a rejected, unrated file under the
    /// contact sheet, which would otherwise wear an empty pill beside its cross.
    /// </summary>
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    public void BadgeShowsForARatingOrForAFlagItStillCarries(
        bool hasRating, bool hasFlag, bool verdictInBadge, bool expected)
    {
        Assert.Equal(expected, TileBadgeConverter.IsVisible(hasRating, hasFlag, verdictInBadge));
    }

    // ---- The film edge --------------------------------------------------------------------------

    /// <summary>
    /// The holes are centred: the same margin at both ends, and never a hole cut off by the end
    /// of the strip.
    /// </summary>
    [Fact]
    public void PerforationsAreCentredWithEqualMargins()
    {
        const double length = 300;
        const double hole = 8;
        const double pitch = 14;

        var offsets = FilmEdge.Layout(length, hole, pitch);

        Assert.NotEmpty(offsets);
        Assert.Equal(offsets[0], length - (offsets[^1] + hole), precision: 9);
        Assert.All(offsets, offset => Assert.True(offset >= 0 && offset + hole <= length));

        for (var i = 1; i < offsets.Length; i++)
        {
            Assert.Equal(pitch, offsets[i] - offsets[i - 1], precision: 9);
        }
    }

    [Fact]
    public void AStripTooShortForOneHoleHasNone()
    {
        Assert.Empty(FilmEdge.Layout(6, hole: 8, pitch: 14));
        Assert.Empty(FilmEdge.Layout(0, hole: 8, pitch: 14));
    }

    [Fact]
    public void AStripExactlyOneHoleLongHasOneCentredHole()
    {
        var offsets = FilmEdge.Layout(8, hole: 8, pitch: 14);

        Assert.Equal([0.0], offsets);
    }

    // ---- The splitters --------------------------------------------------------------------------

    /// <summary>Four points is the line the application has always drawn; the film edge needs room.</summary>
    [Fact]
    public void SplittersWidenOnlyUnderTheContactSheet()
    {
        Assert.Equal(4, AppThemes.SplitterWidthFor(contactSheet: false));
        Assert.True(AppThemes.SplitterWidthFor(contactSheet: true) > 4);
    }
}
