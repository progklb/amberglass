using System.Collections.Immutable;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

/// <summary>One photograph, and the files it arrived as.</summary>
/// <param name="Face">The file the grid draws.</param>
/// <param name="Hidden">Its companions, in name order. Empty for a file that shares its name with nothing.</param>
public sealed record MediaStackGroup(MediaFile Face, ImmutableArray<MediaFile> Hidden)
{
    public bool IsStack => !Hidden.IsDefaultOrEmpty;
}

/// <summary>
/// Collapses files that are the same photograph into one tile.
///
/// A camera set to RAW+JPEG writes two files per frame with one name between them, and a folder of
/// them reads as twice as many photographs as were taken. Every judgement then has to be made
/// twice, or made once and silently not apply to the other half.
///
/// <para>The test is the name, not the kind: same folder, same name before the extension. That is
/// what a camera guarantees about a pair and what a photographer means by "the same shot". It also
/// covers the cases a rule about RAW and JPEG would miss — a DNG beside the CR2 it was converted
/// from is the same frame too, and two tiles for it are two tiles too many.</para>
///
/// <para>Video never stacks. A clip that happens to share a name with a still is not the same
/// recording, and hiding one behind the other would be a guess.</para>
/// </summary>
public static class MediaStacking
{
    /// <summary>
    /// What makes two files the same photograph, or null for a file that never stacks.
    ///
    /// Folder and name, because the name alone would collapse every IMG_0001 in a library into
    /// one tile. Compared case-insensitively: a camera writes <c>IMG_1.CR2</c> beside
    /// <c>IMG_1.JPG</c>, and some write <c>.jpg</c>.
    /// </summary>
    public static string? KeyOf(MediaFile file)
    {
        if (file.MediaType != MediaType.Image)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(file.FullPath);

        // A separator no filename may contain, so a folder ending in the name of another cannot
        // produce the same key by concatenation.
        return $"{folder}\0{name}";
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should be the tile rather than <paramref name="current"/>.
    ///
    /// The asked-for kind first; then the name, so a group of three settles somewhere predictable
    /// and the same folder always draws the same tile. Never true for an equal pair, which is what
    /// keeps a rebuild from reordering what a scan already settled.
    /// </summary>
    public static bool IsBetterFace(MediaFile candidate, MediaFile current, StackShows shows)
    {
        var byKind = Rank(candidate, shows) - Rank(current, shows);

        return byKind != 0
            ? byKind < 0
            : string.Compare(candidate.FileName, current.FileName, StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>0 for the kind asked for, 1 for the other. Nothing else distinguishes a face.</summary>
    private static int Rank(MediaFile file, StackShows shows)
        => MediaTypeRegistry.IsRaw(file.FullPath) == (shows == StackShows.Raw) ? 0 : 1;

    /// <summary>
    /// Groups a list in one pass, keeping each group where its first file appeared.
    ///
    /// Position comes from the first file seen rather than from the face, so choosing to show the
    /// JPEG instead of the RAW changes which picture is drawn and not where it sits. A grid that
    /// reshuffled itself when that was switched would be unreadable.
    /// </summary>
    public static ImmutableArray<MediaStackGroup> Arrange(IReadOnlyList<MediaFile> files, StackShows shows)
    {
        var order = new List<string?>();
        var groups = new Dictionary<string, List<MediaFile>>(StringComparer.OrdinalIgnoreCase);
        var loners = new List<MediaFile>();

        foreach (var file in files)
        {
            if (KeyOf(file) is not { } key)
            {
                order.Add(null);
                loners.Add(file);
                continue;
            }

            if (!groups.TryGetValue(key, out var members))
            {
                members = [];
                groups[key] = members;
                order.Add(key);
            }

            members.Add(file);
        }

        var result = ImmutableArray.CreateBuilder<MediaStackGroup>(order.Count);
        var next = 0;

        foreach (var key in order)
        {
            if (key is null)
            {
                result.Add(new MediaStackGroup(loners[next++], []));
                continue;
            }

            var members = groups[key];
            var face = members[0];

            foreach (var member in members)
            {
                if (IsBetterFace(member, face, shows))
                {
                    face = member;
                }
            }

            result.Add(new MediaStackGroup(
                face,
                members
                    .Where(m => !ReferenceEquals(m, face))
                    .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray()));
        }

        return result.ToImmutable();
    }
}
