using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>
/// Decides which raw metadata tags a filter box shows.
///
/// Deliberately not the query language the search bar uses. That one searches a library and has
/// fields, quoting and negation; this one narrows a list of a couple of hundred rows already on
/// screen, where anything more than "type a bit of what you remember" would be in the way.
/// </summary>
public static class RawMetadataFilter
{
    /// <summary>
    /// True when every whitespace-separated term appears somewhere in the tag — its qualified name
    /// or its value, either will do.
    ///
    /// Every term must match, so terms narrow rather than widen: "exif iso" finds the ISO tag in
    /// the EXIF group and not the sixty other EXIF tags. Each term may match either field, which is
    /// what makes "iso 200" work — the name carries the first, the value the second.
    /// </summary>
    public static bool Matches(RawMetadataTag tag, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        foreach (var term in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!tag.QualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !tag.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
