using System.Collections.Immutable;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>
/// Works out which of a workspace's keywords the library is missing.
/// </summary>
public static class KeywordImport
{
    /// <summary>
    /// What importing <paramref name="found"/> would add.
    ///
    /// <para><b>Matched on the leaf name, wherever it sits in the library.</b> This application
    /// writes leaf names to files — ticking "Bush" under "Subject" writes "Bush", never
    /// "Subject|Bush" — so the catalog hands back "Bush" flat, and matching on the whole path found
    /// nothing and filed a second, top-level "Bush" beside the one already under Subject. Importing
    /// twice built a flat shadow of a vocabulary that had been arranged by hand.</para>
    ///
    /// <para>A keyword arranged under a group is the same keyword whatever it is filed under, which
    /// is the rule the metadata panel already works by, so where it sits is the user's arrangement
    /// and an import has no business second-guessing it.</para>
    /// </summary>
    public static ImportPlan Plan(KeywordLibrary library, IEnumerable<string> found)
    {
        var known = library.AllNames();
        var toAdd = ImmutableArray.CreateBuilder<string>();
        var already = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in found)
        {
            var keyword = raw?.Trim() ?? string.Empty;
            if (keyword.Length == 0)
            {
                continue;
            }

            var leaf = LeafOf(keyword);
            if (leaf.Length == 0 || !seen.Add(leaf))
            {
                continue;
            }

            if (known.Contains(leaf))
            {
                already.Add(leaf);
            }
            else
            {
                // The incoming form, not the leaf: a hierarchical "Subject|animal" from another tool
                // still deserves to be filed under its parent.
                toAdd.Add(keyword);
            }
        }

        return new ImportPlan(toAdd.ToImmutable(), already.ToImmutable());
    }

    /// <summary>Applies a plan. Existing structure wins; nothing already filed is moved.</summary>
    public static KeywordLibrary Apply(KeywordLibrary library, ImportPlan plan)
        => plan.HasAnythingToAdd ? library.MergedWith(plan.ToAdd) : library;

    /// <summary>
    /// The last segment of a keyword written as a path. Both separators, because both are in the
    /// wild — Lightroom writes <c>|</c> and plenty of people simply type slashes.
    /// </summary>
    internal static string LeafOf(string keyword)
    {
        var parts = keyword.Split(['|', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }
}
