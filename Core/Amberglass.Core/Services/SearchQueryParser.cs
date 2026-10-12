using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>
/// Turns a typed query into a <see cref="SearchQuery"/>.
///
/// The fields it accepts, their short forms and their descriptions all come from
/// <see cref="SearchFields"/> — <c>keyword:motorcycle</c> and <c>k:motorcycle</c> are the same thing.
/// Terms combine with implicit AND; a literal <c>AND</c> is accepted and ignored. Bare words become
/// free text. Anything unrecognised is collected rather than dropped, so the UI can tell the user
/// their filter did nothing instead of quietly returning the wrong results.
/// </summary>
public static class SearchQueryParser
{
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchQuery.Empty;
        }

        var freeText = ImmutableArray.CreateBuilder<string>();
        var keywords = ImmutableArray.CreateBuilder<KeywordFilter>();
        var cameras = ImmutableArray.CreateBuilder<string>();
        var labels = ImmutableArray.CreateBuilder<string>();
        var unlabelled = false;
        var flags = ImmutableArray.CreateBuilder<MediaFlag>();
        var fileNames = ImmutableArray.CreateBuilder<string>();
        var lenses = ImmutableArray.CreateBuilder<string>();
        var unrecognised = ImmutableArray.CreateBuilder<string>();

        var kinds = ImmutableArray.CreateBuilder<MediaKind>();
        var orientations = ImmutableArray.CreateBuilder<MediaOrientation>();
        RatingFilter? rating = null;
        DateFilter? captureDate = null;

        foreach (var token in Tokenize(text))
        {
            if (string.Equals(token, "AND", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = token.IndexOf(':');
            if (separator <= 0)
            {
                freeText.Add(Unquote(token));
                continue;
            }

            var field = token[..separator].ToLowerInvariant();
            var value = Unquote(token[(separator + 1)..]);

            if (string.IsNullOrWhiteSpace(value))
            {
                unrecognised.Add(token);
                continue;
            }

            // Resolved through the shared catalogue so the short forms, the help in the filter popup
            // and what actually parses cannot drift apart.
            switch (SearchFields.Resolve(field))
            {
                case "keyword":
                    // Comma means "any of these", as it does for type. Repeating the field still
                    // means "all of these", so both questions can be asked.
                    var words = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (words.Length > 0)
                    {
                        keywords.Add(KeywordFilter.Of(words));
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "label":
                    // Union rather than AND: a file has one label, so two of them can only be a
                    // choice between them. Comma does the same thing, as everywhere else.
                    foreach (var label in value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (string.Equals(label, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            unlabelled = true;
                        }
                        else if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                        {
                            labels.Add(label);
                        }
                    }

                    break;

                case "flag":
                    if (TryParseFlags(value, out var parsedFlags))
                    {
                        foreach (var parsedFlag in parsedFlags.Where(f => !flags.Contains(f)))
                        {
                            flags.Add(parsedFlag);
                        }
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "filename":
                    fileNames.Add(value);
                    break;

                case "camera":
                    cameras.Add(value);
                    break;

                case "lens":
                    lenses.Add(value);
                    break;

                case "type":
                    // Comma means "any of these": a file is one kind, so t:raw,video can only
                    // sensibly be read as either.
                    if (TryParseKinds(value, out var parsedKinds))
                    {
                        foreach (var kind in parsedKinds)
                        {
                            if (!kinds.Contains(kind))
                            {
                                kinds.Add(kind);
                            }
                        }
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "orientation":
                    // Any of these, as with type: a picture is one shape, so o:portrait,square can
                    // only sensibly mean either.
                    if (TryParseOrientations(value, out var parsedOrientations))
                    {
                        foreach (var orientation in parsedOrientations)
                        {
                            if (!orientations.Contains(orientation))
                            {
                                orientations.Add(orientation);
                            }
                        }
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "rating":
                    if (TryParseRating(value, out var parsedRating))
                    {
                        rating = parsedRating;
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "date":
                    if (TryParseDate(value, out var parsedDate))
                    {
                        captureDate = parsedDate;
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                default:
                    // An unknown field is treated as free text rather than discarded — someone
                    // searching for "http://example.com" should still get results.
                    freeText.Add(Unquote(token));
                    break;
            }
        }

        return new SearchQuery
        {
            FreeText = freeText.ToImmutable(),
            Keywords = keywords.ToImmutable(),
            Labels = labels.ToImmutable(),
            IncludeUnlabelled = unlabelled,
            Flags = flags.ToImmutable(),
            FileNames = fileNames.ToImmutable(),
            Cameras = cameras.ToImmutable(),
            Lenses = lenses.ToImmutable(),
            Kinds = kinds.ToImmutable(),
            Orientations = orientations.ToImmutable(),
            Rating = rating,
            CaptureDate = captureDate,
            UnrecognisedTerms = unrecognised.ToImmutable()
        };
    }

    /// <summary>
    /// Splits on whitespace but keeps quoted runs together, so <c>lens:"RF 100-500"</c> survives as
    /// one token.
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                current.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
            ? value[1..^1]
            : value.Replace("\"", string.Empty);

    /// <summary>
    /// Reads one or more kinds from a comma-separated value. All of them must be understood — half
    /// a filter silently applied is worse than being told the whole term was not understood.
    /// </summary>
    /// <summary>
    /// Reads portrait, landscape or square, with the spellings a photographer would reach for.
    /// </summary>
    private static bool TryParseOrientations(string value, out IReadOnlyList<MediaOrientation> orientations)
    {
        var parsed = new List<MediaOrientation>();

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "portrait" or "vertical" or "tall":
                    parsed.Add(MediaOrientation.Portrait);
                    break;

                case "landscape" or "horizontal" or "wide":
                    parsed.Add(MediaOrientation.Landscape);
                    break;

                case "square":
                    parsed.Add(MediaOrientation.Square);
                    break;

                default:
                    orientations = [];
                    return false;
            }
        }

        orientations = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseKinds(string value, out IReadOnlyList<MediaKind> kinds)
    {
        var parsed = new List<MediaKind>();

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "video" or "videos" or "movie":
                    parsed.Add(MediaKind.Video);
                    break;

                case "raw":
                    parsed.Add(MediaKind.Raw);
                    break;

                case "jpg" or "jpeg":
                    parsed.Add(MediaKind.Jpeg);
                    break;

                // "image" is every still, raw or not — which is what it meant before raw became
                // separately selectable, so an old query keeps working.
                case "image" or "images" or "photo" or "photos" or "still":
                    parsed.Add(MediaKind.Raw);
                    parsed.Add(MediaKind.Jpeg);
                    break;

                default:
                    kinds = [];
                    return false;
            }
        }

        kinds = parsed;
        return parsed.Count > 0;
    }

    /// <summary>
    /// Reads cull flags. The words are the ones a photographer would say rather than digiKam's
    /// numbers, and "none" is a real answer — "what have I not looked at yet" is the question that
    /// makes a cull pass finishable.
    /// </summary>
    private static bool TryParseFlags(string value, out IReadOnlyList<MediaFlag> flags)
    {
        var parsed = new List<MediaFlag>();

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "accepted" or "accept" or "pick" or "picked" or "keep":
                    parsed.Add(MediaFlag.Accepted);
                    break;

                case "rejected" or "reject" or "rejects":
                    parsed.Add(MediaFlag.Rejected);
                    break;

                case "pending" or "maybe":
                    parsed.Add(MediaFlag.Pending);
                    break;

                case "none" or "unflagged":
                    parsed.Add(MediaFlag.None);
                    break;

                default:
                    flags = [];
                    return false;
            }
        }

        flags = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseRating(string value, out RatingFilter? filter)
    {
        var (op, remainder) = SplitOperator(value);

        if (int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 0 and <= 5)
        {
            filter = new RatingFilter(op, parsed);
            return true;
        }

        filter = null;
        return false;
    }

    private static bool TryParseDate(string value, out DateFilter? filter)
    {
        var (op, remainder) = SplitOperator(value);

        // A bare year means the whole year, which is what someone typing "date:2024" expects.
        if (remainder.Length == 4 &&
            int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) &&
            year is > 1800 and < 3000)
        {
            filter = op == ComparisonOperator.Equal
                ? new DateFilter(ComparisonOperator.GreaterThanOrEqual, new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero))
                : new DateFilter(op, new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero));
            return true;
        }

        if (DateTimeOffset.TryParse(remainder, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            filter = new DateFilter(op, parsed);
            return true;
        }

        filter = null;
        return false;
    }

    private static (ComparisonOperator Operator, string Remainder) SplitOperator(string value)
    {
        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            return (ComparisonOperator.GreaterThanOrEqual, value[2..].Trim());
        }

        if (value.StartsWith("<=", StringComparison.Ordinal))
        {
            return (ComparisonOperator.LessThanOrEqual, value[2..].Trim());
        }

        if (value.StartsWith('>'))
        {
            return (ComparisonOperator.GreaterThan, value[1..].Trim());
        }

        if (value.StartsWith('<'))
        {
            return (ComparisonOperator.LessThan, value[1..].Trim());
        }

        return (ComparisonOperator.Equal, value.Trim());
    }
}
