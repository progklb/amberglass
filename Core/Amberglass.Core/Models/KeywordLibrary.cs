using System.Collections.Immutable;

namespace Amberglass.Core.Models;

/// <summary>
/// A keyword, and the keywords filed under it.
///
/// Every node carries a name and can be applied to a photograph — including one with children. A
/// two-level library reads as categories and keywords ("Subject" → "animal"), but nothing enforces
/// that, because the useful cases do not divide cleanly: "Golden hour" is both a heading for
/// "sunrise" and "sunset" and a perfectly good keyword on its own.
///
/// <b>What gets written to a file is <see cref="Name"/> alone.</b> Ticking "wide" under "Shot type"
/// writes "wide", never "Shot type|wide". The hierarchy is an organising device inside this
/// application and nothing else — which is how Bridge treats it, and what keeps the tags readable by
/// every other tool. See <see cref="KeywordLibrary"/> for what follows from that.
/// </summary>
public sealed record KeywordNode
{
    public required string Name { get; init; }

    public ImmutableArray<KeywordNode> Children { get; init; } = [];

    public bool HasChildren => Children.Length > 0;

    /// <summary>This node and everything beneath it, depth first.</summary>
    public IEnumerable<KeywordNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var descendant in Children.SelectMany(child => child.SelfAndDescendants()))
        {
            yield return descendant;
        }
    }
}

/// <summary>
/// The user's own vocabulary, arranged however they think about their work.
///
/// Kept apart from the keywords actually written to files. This is a palette to pick from — the
/// photographs remain the authority on what they are tagged with, and a keyword can always be typed
/// by hand whether or not it appears here. Deleting the library changes no metadata.
///
/// <b>The grouping never reaches the files.</b> A keyword is written as its own bare name, so what
/// this application tags is exactly what Bridge, Lightroom or exiftool will read back. Two
/// consequences worth knowing, both of which follow from names being the identity:
/// <list type="bullet">
/// <item>The same name filed in two different groups is <b>one keyword</b>. Ticking either applies
/// the same tag, and a file carrying it matches both places in the tree.</item>
/// <item>Renaming a group renames a keyword. It does not re-tag anything already written — the files
/// keep whatever name was current when they were tagged.</item>
/// </list>
/// </summary>
public sealed record KeywordLibrary
{
    public static readonly KeywordLibrary Empty = new();

    public ImmutableArray<KeywordNode> Roots { get; init; } = [];

    public bool IsEmpty => Roots.Length == 0;

    /// <summary>Every node in the library, depth first, parents before their children.</summary>
    public IEnumerable<KeywordNode> Flatten() => Roots.SelectMany(root => root.SelfAndDescendants());

    /// <summary>
    /// Every distinct keyword the library offers, in no particular order.
    ///
    /// Distinct by name and ignoring case, because that is what identity means here — the same word
    /// in two groups is one keyword, not two.
    /// </summary>
    public IReadOnlySet<string> AllNames()
        => Flatten().Select(node => node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public int Count => Flatten().Count();

    /// <summary>
    /// Every path in the tree, written with <c>|</c> — the form <see cref="FromFlat"/> reads back.
    /// Round tripping through this is how merging works: flatten both sides, union, rebuild.
    ///
    /// <b>Not a keyword format.</b> This is how the shape of the library is expressed for storage and
    /// merging; nothing written to a photograph ever looks like this. The separator is here only
    /// because it is what other tools use when they do write hierarchies, so importing and exporting
    /// the library speaks a form they already understand.
    /// </summary>
    public IEnumerable<string> ToPaths()
    {
        return Roots.SelectMany(root => Walk(root, prefix: null));

        static IEnumerable<string> Walk(KeywordNode node, string? prefix)
        {
            var path = prefix is null ? node.Name : $"{prefix}|{node.Name}";

            // Parents are emitted as well as their children. A parent is a keyword in its own right,
            // so dropping it here would quietly delete it on the next merge.
            yield return path;

            foreach (var descendant in node.Children.SelectMany(child => Walk(child, path)))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="keywords"/> without disturbing what is already filed. Existing structure
    /// wins: importing must never rearrange a vocabulary someone has organised by hand.
    /// </summary>
    public KeywordLibrary MergedWith(IEnumerable<string> keywords)
        => FromFlat(ToPaths().Concat(keywords));

    /// <summary>
    /// Builds a library from flat keywords, filing anything written as a path — "Subject|animal" or
    /// "Subject/animal" — under the appropriate parent.
    ///
    /// Both separators are accepted because both are in the wild: Lightroom exports hierarchical
    /// keywords with <c>|</c>, while plenty of people simply type slashes. Everything else becomes a
    /// root, which is the honest outcome for a flat vocabulary — the user can rearrange it afterwards,
    /// and having it listed beats having to retype it.
    /// </summary>
    public static KeywordLibrary FromFlat(IEnumerable<string> keywords)
    {
        var roots = new List<MutableNode>();

        foreach (var keyword in keywords)
        {
            var parts = keyword
                .Split(['|', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            var level = roots;
            MutableNode? node = null;

            foreach (var part in parts)
            {
                node = level.FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));

                if (node is null)
                {
                    node = new MutableNode(part);
                    level.Add(node);
                }

                level = node.Children;
            }
        }

        // Roots sorted like every other level. Without this the order is whatever the source
        // happened to produce — for an import, catalog usage counts, which reads as no order at all.
        return new KeywordLibrary
        {
            Roots = [.. roots.OrderBy(root => root.Name, StringComparer.OrdinalIgnoreCase).Select(root => root.ToNode())]
        };
    }

    private sealed class MutableNode(string name)
    {
        public string Name { get; } = name;

        public List<MutableNode> Children { get; } = [];

        public KeywordNode ToNode() => new()
        {
            Name = Name,
            Children = [.. Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.ToNode())]
        };
    }
}
