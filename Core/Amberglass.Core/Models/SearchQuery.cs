using System.Collections.Immutable;

namespace Amberglass.Core.Models;

public enum ComparisonOperator
{
    Equal,
    GreaterThanOrEqual,
    LessThanOrEqual,
    GreaterThan,
    LessThan
}

public sealed record RatingFilter(ComparisonOperator Operator, int Value);

public sealed record DateFilter(ComparisonOperator Operator, DateTimeOffset Value);

/// <summary>
/// One keyword condition: a file matches it by carrying any of these words.
///
/// A list rather than a single word because <c>k:sand,dust</c> has to mean "either", while
/// <c>k:sand k:dust</c> still means "both" — two useful questions that need telling apart.
/// </summary>
public sealed record KeywordFilter(ImmutableArray<string> AnyOf)
{
    public static KeywordFilter Of(params string[] words) => new([.. words]);
}

/// <summary>
/// A parsed search. Everything here is structured — the parser turns the user's typed string into
/// this, and the catalog turns this into SQL. Nothing downstream re-parses text.
/// </summary>
public sealed record SearchQuery
{
    public static readonly SearchQuery Empty = new();

    /// <summary>Bare words, matched across title, description, headline, keywords and creator.</summary>
    public ImmutableArray<string> FreeText { get; init; } = [];

    /// <summary>
    /// Keyword filters. Each one must be satisfied, and a filter is satisfied by any of its words —
    /// so repeating the field means "all of these" and a comma means "any of these".
    /// </summary>
    public ImmutableArray<KeywordFilter> Keywords { get; init; } = [];

    /// <summary>
    /// Colour labels to include, matched case-insensitively. Several mean "any of these": a file
    /// carries one label, so asking for two could only sensibly be a choice between them.
    /// </summary>
    public ImmutableArray<string> Labels { get; init; } = [];

    /// <summary>
    /// Whether files carrying no label at all should be included. Separate from
    /// <see cref="Labels"/> because "unlabelled" is a question about absence, which no label name
    /// can express.
    /// </summary>
    public bool IncludeUnlabelled { get; init; }

    /// <summary>
    /// Cull flags to include. Several mean "any of these", since a file carries one flag.
    /// </summary>
    public ImmutableArray<MediaFlag> Flags { get; init; } = [];

    /// <summary>Filename fragments, matched anywhere in the name.</summary>
    public ImmutableArray<string> FileNames { get; init; } = [];

    public ImmutableArray<string> Cameras { get; init; } = [];

    public ImmutableArray<string> Lenses { get; init; } = [];

    /// <summary>
    /// What kinds of file to include. Empty means all of them. Several combine as "any of these" —
    /// a file is one kind, so asking for RAW and video can only sensibly mean either.
    /// </summary>
    public ImmutableArray<MediaKind> Kinds { get; init; } = [];

    /// <summary>Any of these, as with kinds — o:portrait,square is a reasonable thing to ask.</summary>
    public ImmutableArray<MediaOrientation> Orientations { get; init; } = [];

    public RatingFilter? Rating { get; init; }

    public DateFilter? CaptureDate { get; init; }

    /// <summary>Anything the parser could not make sense of, so the UI can say so rather than silently ignoring it.</summary>
    public ImmutableArray<string> UnrecognisedTerms { get; init; } = [];

    public bool IsEmpty =>
        Orientations.IsDefaultOrEmpty &&
        FreeText.IsDefaultOrEmpty && Keywords.IsDefaultOrEmpty && Cameras.IsDefaultOrEmpty &&
        Labels.IsDefaultOrEmpty && !IncludeUnlabelled && Flags.IsDefaultOrEmpty && FileNames.IsDefaultOrEmpty &&
        Lenses.IsDefaultOrEmpty && Kinds.IsDefaultOrEmpty && Rating is null && CaptureDate is null;
}

/// <summary>One row of a search result — enough to render a tile without touching the disk.</summary>
public sealed record SearchHit(
    string FullPath,
    string FileName,
    MediaType MediaType,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset CreatedUtc,
    int? Rating,
    string? Title)
{
    public MediaFile ToMediaFile() => new()
    {
        FullPath = FullPath,
        FileName = FileName,
        MediaType = MediaType,
        SizeBytes = SizeBytes,
        ModifiedUtc = ModifiedUtc,
        CreatedUtc = CreatedUtc
    };
}
