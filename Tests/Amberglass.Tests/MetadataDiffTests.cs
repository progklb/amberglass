using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The review of a pending change: what the Changes window shows for a file.
/// </summary>
public class MetadataDiffTests
{
    private static readonly EditableMetadata Base = new()
    {
        Title = "Dune at dusk",
        Rating = 3,
        Keywords = ["sand", "dusk", "namibia"]
    };

    [Fact]
    public void NothingChangedIsEmpty()
    {
        var diff = MetadataDiff.Between(Base, Base with { });

        Assert.True(diff.IsEmpty);
        Assert.Equal(string.Empty, diff.Summary);
    }

    /// <summary>A file whose rating changed has one thing to say, not nine.</summary>
    [Fact]
    public void OnlyFieldsThatDifferAreListed()
    {
        var diff = MetadataDiff.Between(Base, Base with { Rating = 5 });

        var change = Assert.Single(diff.Fields);
        Assert.Equal(MetadataConflictDetector.RatingField, change.Field);
        Assert.Equal("3 stars", change.Was);
        Assert.Equal("5 stars", change.Now);
        Assert.False(diff.HasKeywordChanges);
    }

    /// <summary>
    /// Null is a value. Going from no title to a title is a change, and so is clearing one; the
    /// store distinguishes the two and so must the review.
    /// </summary>
    [Fact]
    public void NotPresentIsShownAsAValue()
    {
        var gained = MetadataDiff.Between(Base with { Title = null }, Base);
        var lost = MetadataDiff.Between(Base, Base with { Title = null });

        Assert.Null(Assert.Single(gained.Fields).Was);
        Assert.Equal("Dune at dusk", Assert.Single(gained.Fields).Now);

        Assert.Equal("Dune at dusk", Assert.Single(lost.Fields).Was);
        Assert.Null(Assert.Single(lost.Fields).Now);
    }

    [Fact]
    public void KeywordsAreDiffedAsAList()
    {
        var diff = MetadataDiff.Between(Base, Base with { Keywords = ["sand", "namibia", "sossusvlei", "dune"] });

        Assert.Empty(diff.Fields);
        Assert.Equal(["sossusvlei", "dune"], diff.KeywordsAdded.ToArray());
        Assert.Equal(["dusk"], diff.KeywordsRemoved.ToArray());
    }

    /// <summary>The same words in a different order are the same keywords.</summary>
    [Fact]
    public void ReorderedKeywordsAreNotAChange()
    {
        var diff = MetadataDiff.Between(Base, Base with { Keywords = ["namibia", "sand", "dusk"] });

        Assert.True(diff.IsEmpty);
    }

    /// <summary>Ordinal, because the file will store both spellings as two keywords.</summary>
    [Fact]
    public void KeywordCaseIsAChange()
    {
        var diff = MetadataDiff.Between(Base, Base with { Keywords = ["Sand", "dusk", "namibia"] });

        Assert.Equal(["Sand"], diff.KeywordsAdded.ToArray());
        Assert.Equal(["sand"], diff.KeywordsRemoved.ToArray());
    }

    [Fact]
    public void SummaryNamesWhatChanged()
    {
        var diff = MetadataDiff.Between(Base, Base with
        {
            Rating = 4,
            Label = "Yellow",
            Keywords = ["sand", "dusk", "namibia", "sossusvlei", "dune"]
        });

        Assert.Equal("rating · label · +2 keywords", diff.Summary);
    }

    [Fact]
    public void SummaryCountsAddedAndRemovedKeywordsSeparately()
    {
        var diff = MetadataDiff.Between(Base, Base with { Keywords = ["sand", "dune"] });

        Assert.Equal("+1 −2 keywords", diff.Summary);
    }

    [Fact]
    public void ASingleKeywordIsSingular()
    {
        var diff = MetadataDiff.Between(Base, Base with { Keywords = ["sand", "dusk", "namibia", "dune"] });

        Assert.Equal("+1 keyword", diff.Summary);
    }

    /// <summary>
    /// A cleared flag is a decision, shown as one. It is the difference between "nobody said" and
    /// "somebody said no", which the store keeps and the review must not flatten.
    /// </summary>
    [Fact]
    public void AClearedFlagReadsAsClearedNotAsNothing()
    {
        var diff = MetadataDiff.Between(Base with { Flag = MediaFlag.Rejected }, Base with { Flag = MediaFlag.None });

        var change = Assert.Single(diff.Fields);
        Assert.Equal(MetadataDiff.FlagField, change.Field);
        Assert.Equal("Rejected", change.Was);
        Assert.Equal("Cleared", change.Now);
    }

    /// <summary>An explicit zero is a value the file carries; it reads as unrated, not as "0 stars".</summary>
    [Fact]
    public void AZeroRatingReadsAsUnrated()
    {
        var diff = MetadataDiff.Between(Base with { Rating = 0 }, Base with { Rating = 3 });

        Assert.Equal("Unrated", Assert.Single(diff.Fields).Was);
    }

    [Fact]
    public void FieldsComeInTheInspectorsOrder()
    {
        var diff = MetadataDiff.Between(Base, Base with { Copyright = "K.", Title = "x", Rating = 1, Headline = "h" });

        Assert.Equal(
            [MetadataConflictDetector.TitleField, MetadataConflictDetector.HeadlineField,
             MetadataConflictDetector.RatingField, MetadataConflictDetector.CopyrightField],
            diff.Fields.Select(f => f.Field).ToArray());
        Assert.Equal("1 star", diff.Fields[2].Now);
    }
}
