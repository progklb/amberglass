namespace Amberglass.Core.Models;

/// <summary>
/// The three judgements a tile shows: how good, whether it is in or out, and which pile it is on.
///
/// Kept apart from <see cref="EditableMetadata"/> because that is what a file <em>has</em>, read one
/// file at a time; this is what the grid <em>draws</em>, read for a whole folder at once.
/// </summary>
public readonly record struct MediaMarks(int? Rating, MediaFlag Flag, string? Label)
{
    public static readonly MediaMarks None = new(null, MediaFlag.None, null);

    public bool IsEmpty => Rating is null or 0 && Flag == MediaFlag.None && string.IsNullOrWhiteSpace(Label);
}
