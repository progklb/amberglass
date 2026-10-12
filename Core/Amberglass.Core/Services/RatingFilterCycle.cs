using System.Globalization;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>What the rating stars in the filter popup are currently asking for.</summary>
/// <param name="Stars">The rating clicked, or zero when nothing is being filtered.</param>
/// <param name="Exact">
/// True for "exactly this many", false for "this many and up". Only meaningful when
/// <paramref name="Stars"/> is set.
/// </param>
public readonly record struct RatingFilterState(int Stars, bool Exact)
{
    public static readonly RatingFilterState None = new(0, false);

    public bool IsSet => Stars > 0;
}

/// <summary>
/// The three-state behaviour of a rating star: once for "and up", again for "exactly", again to
/// clear.
///
/// Pulled out of the ViewModel because it is the sort of small state machine that is easy to get
/// subtly wrong — clicking a <i>different</i> star while "exactly" is showing has to start that star
/// afresh rather than inherit the exactness, and no amount of clicking should ever leave a filter set
/// to zero stars. Both are one line here and awkward to reach through the interface.
/// </summary>
public static class RatingFilterCycle
{
    public static RatingFilterState Next(RatingFilterState current, int clicked)
    {
        if (clicked <= 0)
        {
            return RatingFilterState.None;
        }

        // A different star always starts its own cycle: clicking 4 while "exactly 3" is showing
        // means "4 and up", not "exactly 4".
        if (current.Stars != clicked)
        {
            return new RatingFilterState(clicked, Exact: false);
        }

        return current.Exact
            ? RatingFilterState.None
            : new RatingFilterState(clicked, Exact: true);
    }

    /// <summary>
    /// Whether the star at <paramref name="position"/> should be drawn filled.
    ///
    /// "And up" fills every star to the one chosen, the way a rating is normally drawn. "Exactly"
    /// fills only the one chosen — otherwise the two states are the same picture, and the filter
    /// would depend on the word beside it to be understood at all.
    /// </summary>
    public static bool IsStarFilled(RatingFilterState state, int position)
    {
        if (!state.IsSet)
        {
            return false;
        }

        return state.Exact ? position == state.Stars : position <= state.Stars;
    }

    /// <summary>The query term for a state, or null when nothing should be written.</summary>
    public static string? ToTerm(RatingFilterState state)
    {
        if (!state.IsSet)
        {
            return null;
        }

        var stars = state.Stars.ToString(CultureInfo.InvariantCulture);

        // A bare number already parses as equality, so "exactly" needs no operator.
        return state.Exact ? stars : $">={stars}";
    }

    /// <summary>
    /// The state a parsed query implies, so the stars show what is being filtered however it was
    /// arrived at. Anything other than "at least" or "exactly" leaves them dark rather than
    /// claiming a filter the query does not have — there is no way to draw "fewer than three stars".
    /// </summary>
    public static RatingFilterState From(RatingFilter? rating) => rating switch
    {
        { Operator: ComparisonOperator.GreaterThanOrEqual } r => new RatingFilterState(r.Value, false),
        { Operator: ComparisonOperator.Equal } r => new RatingFilterState(r.Value, true),
        _ => RatingFilterState.None
    };
}
