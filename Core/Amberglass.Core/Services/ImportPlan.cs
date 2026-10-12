using System.Collections.Immutable;

namespace Amberglass.Core.Services;

/// <summary>
/// What an import would do, worked out before anything is changed so it can be shown and agreed to.
///
/// A library is slow to arrange and easy to spoil, and "Import from workspace" is exactly the sort
/// of button someone presses to find out what it does. Deciding first and applying second is what
/// lets the question be answered without taking the risk.
/// </summary>
/// <param name="ToAdd">The entries that would be added, in the order they would be added.</param>
/// <param name="AlreadyKnown">
/// Those the library already has. Worth showing rather than silently dropping: it is the difference
/// between "this found nothing" and "this found plenty and you have it all".
/// </param>
public sealed record ImportPlan(ImmutableArray<string> ToAdd, ImmutableArray<string> AlreadyKnown)
{
    public static readonly ImportPlan Empty = new([], []);

    /// <summary>How many distinct entries the workspace offered.</summary>
    public int Considered => ToAdd.Length + AlreadyKnown.Length;

    public bool HasAnythingToAdd => ToAdd.Length > 0;
}
