using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The rating star's three states: once for "and up", again for "exactly", again to clear.
/// </summary>
public class RatingFilterCycleTests
{
    [Fact]
    public void OneClickAsksForThatManyAndUp()
    {
        var state = RatingFilterCycle.Next(RatingFilterState.None, 3);

        Assert.Equal(new RatingFilterState(3, Exact: false), state);
        Assert.Equal(">=3", RatingFilterCycle.ToTerm(state));
    }

    [Fact]
    public void ASecondClickAsksForExactlyThatMany()
    {
        var state = RatingFilterCycle.Next(new RatingFilterState(3, false), 3);

        Assert.Equal(new RatingFilterState(3, Exact: true), state);

        // A bare number already parses as equality, so no operator is written.
        Assert.Equal("3", RatingFilterCycle.ToTerm(state));
    }

    [Fact]
    public void AThirdClickClearsIt()
    {
        var state = RatingFilterCycle.Next(new RatingFilterState(3, true), 3);

        Assert.Equal(RatingFilterState.None, state);
        Assert.Null(RatingFilterCycle.ToTerm(state));
    }

    [Fact]
    public void TheCycleReturnsToWhereItStarted()
    {
        var state = RatingFilterState.None;

        for (var i = 0; i < 3; i++)
        {
            state = RatingFilterCycle.Next(state, 4);
        }

        Assert.Equal(RatingFilterState.None, state);
    }

    /// <summary>
    /// A different star starts its own cycle. Inheriting the exactness would mean clicking 4 while
    /// "exactly 3" was showing silently asked for "exactly 4" — a different question from the one
    /// the click looks like it is asking.
    /// </summary>
    [Fact]
    public void ADifferentStarStartsAgainAtAndUp()
    {
        var state = RatingFilterCycle.Next(new RatingFilterState(3, Exact: true), 4);

        Assert.Equal(new RatingFilterState(4, Exact: false), state);
    }

    [Fact]
    public void ADifferentStarStartsAgainEvenFromAndUp()
    {
        var state = RatingFilterCycle.Next(new RatingFilterState(3, Exact: false), 5);

        Assert.Equal(new RatingFilterState(5, Exact: false), state);
    }

    /// <summary>No sequence of clicks may leave a filter asking for zero stars.</summary>
    [Fact]
    public void AFilterIsNeverSetToNoStars()
    {
        var state = RatingFilterState.None;

        foreach (var clicked in (int[])[1, 1, 1, 2, 2, 5, 5, 5, 3])
        {
            state = RatingFilterCycle.Next(state, clicked);
            Assert.True(state.Stars > 0 || !state.IsSet);
            Assert.False(state.IsSet && state.Stars == 0);
        }
    }

    // ---- Reading a typed query back ---------------------------------------------------------------

    [Theory]
    [InlineData(">=4", 4, false)]
    [InlineData("4", 4, true)]
    public void TheStarsFollowATypedQuery(string term, int stars, bool exact)
    {
        var query = SearchQueryParser.Parse($"r:{term}");

        Assert.Equal(new RatingFilterState(stars, exact), RatingFilterCycle.From(query.Rating));
    }

    /// <summary>
    /// There is no way to draw "fewer than three stars", so those queries leave the stars dark rather
    /// than showing a filter that is not what is being asked.
    /// </summary>
    [Theory]
    [InlineData("<3")]
    [InlineData("<=3")]
    [InlineData(">3")]
    public void AQueryTheStarsCannotDrawLeavesThemDark(string term)
    {
        var query = SearchQueryParser.Parse($"r:{term}");

        Assert.NotNull(query.Rating);
        Assert.Equal(RatingFilterState.None, RatingFilterCycle.From(query.Rating));
    }

    [Fact]
    public void NoRatingAtAllLeavesThemDark()
    {
        Assert.Equal(RatingFilterState.None, RatingFilterCycle.From(null));
    }

    /// <summary>What the cycle writes has to be what the parser reads.</summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void WhatIsWrittenIsWhatIsRead(int stars, bool exact)
    {
        var written = RatingFilterCycle.ToTerm(new RatingFilterState(stars, exact));

        var query = SearchQueryParser.Parse($"r:{written}");

        Assert.Equal(new RatingFilterState(stars, exact), RatingFilterCycle.From(query.Rating));
    }
}

/// <summary>
/// Which stars are drawn filled. The two states fill the same count unless they are told apart, so
/// this is what keeps "exactly 4" from looking identical to "4 and up".
/// </summary>
public class RatingFilterStarTests
{
    [Fact]
    public void AndUpFillsEveryStarUpToTheOneChosen()
    {
        var state = new RatingFilterState(4, Exact: false);

        Assert.Equal(
            [true, true, true, true, false],
            Enumerable.Range(1, 5).Select(p => RatingFilterCycle.IsStarFilled(state, p)).ToArray());
    }

    [Fact]
    public void ExactlyFillsOnlyTheOneChosen()
    {
        var state = new RatingFilterState(4, Exact: true);

        Assert.Equal(
            [false, false, false, true, false],
            Enumerable.Range(1, 5).Select(p => RatingFilterCycle.IsStarFilled(state, p)).ToArray());
    }

    [Fact]
    public void NothingIsFilledWhenNothingIsFiltered()
    {
        Assert.All(Enumerable.Range(1, 5), p =>
            Assert.False(RatingFilterCycle.IsStarFilled(RatingFilterState.None, p)));
    }

    /// <summary>
    /// The point of the change: at the same count the two states must not draw the same picture.
    /// </summary>
    [Fact]
    public void TheTwoStatesLookDifferentAtEveryCount()
    {
        foreach (var stars in Enumerable.Range(2, 4))
        {
            var andUp = Enumerable.Range(1, 5)
                .Select(p => RatingFilterCycle.IsStarFilled(new RatingFilterState(stars, false), p)).ToArray();

            var exactly = Enumerable.Range(1, 5)
                .Select(p => RatingFilterCycle.IsStarFilled(new RatingFilterState(stars, true), p)).ToArray();

            Assert.NotEqual(andUp, exactly);
        }
    }

    /// <summary>One star is the exception, and unavoidably so: "1 and up" and "exactly 1" both
    /// fill the first star alone. The label beside them is what separates those two.</summary>
    [Fact]
    public void OneStarIsTheSameEitherWay()
    {
        Assert.True(RatingFilterCycle.IsStarFilled(new RatingFilterState(1, false), 1));
        Assert.True(RatingFilterCycle.IsStarFilled(new RatingFilterState(1, true), 1));
    }
}
