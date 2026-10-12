using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Which colour a label is drawn in, and — the interesting half — where that colour comes from when
/// the label was written by some other application.
/// </summary>
public class LabelColourTests
{
    private static readonly LabelLibrary Bridge = LabelLibrary.Default;

    [Fact]
    public void TheLibraryDecidesWhenItKnowsTheLabel()
        => Assert.Equal("#6ABF52", LabelColours.Resolve(Bridge, "Approved"));

    [Fact]
    public void TheLibraryIsCaseInsensitive()
        => Assert.Equal("#6ABF52", LabelColours.Resolve(Bridge, "approved"));

    /// <summary>
    /// The case this is for. Lightroom's labels are colour words, so a file labelled there arrives
    /// saying "Yellow" — a name Bridge's default library has never heard of.
    /// </summary>
    [Fact]
    public void AColourWordIsRead()
    {
        Assert.Equal("#E8C84A", LabelColours.Resolve(Bridge, "Yellow"));
        Assert.Equal("#E8574A", LabelColours.Resolve(Bridge, "Red"));
        Assert.Equal("#4AA3E8", LabelColours.Resolve(Bridge, "blue"));
    }

    /// <summary>
    /// The word is only a fallback. Someone who has named a label "Yellow" and coloured it something
    /// else made a deliberate choice, and guessing from the word would overrule them.
    /// </summary>
    [Fact]
    public void TheLibraryOutranksTheWord()
    {
        var custom = new LabelLibrary
        {
            Labels = [new LabelDefinition("Yellow", "#123456")]
        };

        Assert.Equal("#123456", LabelColours.Resolve(custom, "Yellow"));
    }

    [Fact]
    public void AWordThatIsNotAColourStillGetsAMark()
    {
        // The file is labelled. Drawing nothing would say it is not.
        Assert.Equal(LabelColours.Unrecognised, LabelColours.Resolve(Bridge, "Sunset"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoLabelIsNoColour(string? label)
        => Assert.Null(LabelColours.Resolve(Bridge, label));

    [Fact]
    public void SurroundingSpaceDoesNotHideAColour()
        => Assert.Equal("#E8C84A", LabelColours.Resolve(Bridge, "  Yellow "));

    [Fact]
    public void WhereTheColourCameFromIsAnswerable()
    {
        // Drives the tooltip: a label this workspace uses that the library has never heard of is
        // worth explaining, and one the user defined is not.
        Assert.True(LabelColours.IsFromTheWord(Bridge, "Yellow"));
        Assert.False(LabelColours.IsFromTheWord(Bridge, "Approved"));
        Assert.False(LabelColours.IsFromTheWord(Bridge, "Sunset"));
    }

    [Fact]
    public void LightroomsWholeDefaultSetIsCovered()
    {
        // The set worth guaranteeing: these five are what Lightroom ships, so a workspace labelled
        // there colours correctly with no library changes at all.
        foreach (var name in (string[])["Red", "Yellow", "Green", "Blue", "Purple"])
        {
            Assert.NotEqual(LabelColours.Unrecognised, LabelColours.Resolve(Bridge, name));
        }
    }
}
