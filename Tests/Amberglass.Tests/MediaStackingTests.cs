using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.UI.ViewModels;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Collapsing a RAW+JPEG pair into one tile: what counts as the same photograph, and which half
/// of it the grid draws.
/// </summary>
public class MediaStackingTests
{
    private static MediaFile At(string path) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        MediaType = path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? MediaType.Video : MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static readonly MediaFile Cr2 = At("/d2/IMG_9700.CR2");
    private static readonly MediaFile Jpg = At("/d2/IMG_9700.JPG");

    [Fact]
    public void APairIsOneTileShowingTheAskedForKind()
    {
        var raw = MediaStacking.Arrange([Cr2, Jpg], StackShows.Raw);
        var jpeg = MediaStacking.Arrange([Cr2, Jpg], StackShows.Jpeg);

        Assert.Equal(Cr2, Assert.Single(raw).Face);
        Assert.Equal([Jpg], Assert.Single(raw).Hidden.ToArray());

        Assert.Equal(Jpg, Assert.Single(jpeg).Face);
        Assert.Equal([Cr2], Assert.Single(jpeg).Hidden.ToArray());
    }

    /// <summary>Switching which half is shown must not move the tile.</summary>
    [Fact]
    public void TheKindShownDoesNotChangeTheOrder()
    {
        var files = new[] { At("/d2/b.JPG"), At("/d2/a.CR2"), At("/d2/b.CR2"), At("/d2/a.JPG") };

        var raw = MediaStacking.Arrange(files, StackShows.Raw);
        var jpeg = MediaStacking.Arrange(files, StackShows.Jpeg);

        Assert.Equal(["b.CR2", "a.CR2"], raw.Select(g => g.Face.FileName).ToArray());
        Assert.Equal(["b.JPG", "a.JPG"], jpeg.Select(g => g.Face.FileName).ToArray());
    }

    [Fact]
    public void AFileThatSharesItsNameWithNothingIsNotAStack()
    {
        var alone = Assert.Single(MediaStacking.Arrange([At("/d2/solo.CR2")], StackShows.Raw));

        Assert.False(alone.IsStack);
        Assert.Empty(alone.Hidden);
    }

    /// <summary>The same name in another folder is another photograph.</summary>
    [Fact]
    public void TheSameNameInAnotherFolderDoesNotStack()
    {
        var groups = MediaStacking.Arrange([At("/d1/IMG_9700.CR2"), At("/d2/IMG_9700.CR2")], StackShows.Raw);

        Assert.Equal(2, groups.Length);
        Assert.All(groups, g => Assert.False(g.IsStack));
    }

    /// <summary>A camera that writes lower case is writing the same pair.</summary>
    [Fact]
    public void CaseDoesNotSeparateAPair()
    {
        var groups = MediaStacking.Arrange([At("/d2/IMG_1.CR2"), At("/d2/img_1.jpg")], StackShows.Raw);

        Assert.True(Assert.Single(groups).IsStack);
    }

    /// <summary>
    /// A DNG beside the CR2 it came from is the same frame. Neither is the asked-for kind more
    /// than the other, so the name settles it — and settles it the same way every time.
    /// </summary>
    [Fact]
    public void TwoRawsOfOneFrameCollapseAndSettleByName()
    {
        var groups = MediaStacking.Arrange([At("/d2/IMG_1.NEF"), At("/d2/IMG_1.DNG")], StackShows.Raw);

        var only = Assert.Single(groups);
        Assert.Equal("IMG_1.DNG", only.Face.FileName);
        Assert.Equal(["IMG_1.NEF"], only.Hidden.Select(f => f.FileName).ToArray());
    }

    /// <summary>
    /// With no RAW in the group, the asked-for kind cannot be had and the name settles it rather
    /// than the group being left uncollapsed.
    /// </summary>
    [Fact]
    public void AGroupWithoutTheAskedForKindStillCollapses()
    {
        var groups = MediaStacking.Arrange([At("/d2/IMG_1.PNG"), At("/d2/IMG_1.JPG")], StackShows.Raw);

        var only = Assert.Single(groups);
        Assert.Equal("IMG_1.JPG", only.Face.FileName);
    }

    /// <summary>
    /// A clip that shares a name with a still is not the same recording. Hiding either behind the
    /// other would be a guess, so video is left out of this entirely.
    /// </summary>
    [Fact]
    public void VideoNeverStacks()
    {
        var groups = MediaStacking.Arrange([At("/d2/IMG_1.mov"), At("/d2/IMG_1.JPG")], StackShows.Jpeg);

        Assert.Equal(2, groups.Length);
        Assert.All(groups, g => Assert.False(g.IsStack));
    }

    /// <summary>Three files of one frame are one tile with two underneath, in name order.</summary>
    [Fact]
    public void AGroupOfThreeHidesTwo()
    {
        var groups = MediaStacking.Arrange(
            [At("/d2/IMG_1.CR2"), At("/d2/IMG_1.JPG"), At("/d2/IMG_1.HEIC")], StackShows.Raw);

        var only = Assert.Single(groups);
        Assert.Equal("IMG_1.CR2", only.Face.FileName);
        Assert.Equal(["IMG_1.HEIC", "IMG_1.JPG"], only.Hidden.Select(f => f.FileName).ToArray());
    }

    /// <summary>
    /// What a tile says about what is under it. The format rather than a bare count, because the
    /// question a photographer asks of a stacked tile is whether the RAW is there.
    /// </summary>
    [Fact]
    public void TheBadgeNamesOneCompanionAndCountsSeveral()
    {
        var tile = new MediaItemViewModel(Jpg, new NoThumbnails());

        Assert.False(tile.IsStacked);
        Assert.Equal(string.Empty, tile.StackBadge);

        tile.Stacked = [Cr2];
        Assert.True(tile.IsStacked);
        Assert.Equal("+CR2", tile.StackBadge);
        Assert.Equal("Also here: IMG_9700.CR2", tile.StackTip);

        tile.Stacked = [Cr2, At("/d2/IMG_9700.HEIC")];
        Assert.Equal("+2", tile.StackBadge);
    }

    private sealed class NoThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file, int maxEdgePixels, ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    [Fact]
    public void NothingInNothingOut()
    {
        Assert.Empty(MediaStacking.Arrange([], StackShows.Raw));
    }

    /// <summary>
    /// The grid builds its stacks a batch at a time as a folder is scanned, rather than arranging
    /// the whole list again on every flush. That incremental rule has to land where Arrange would:
    /// this walks a shuffled folder one file at a time, exactly as the scan does, and compares.
    /// </summary>
    [Theory]
    [InlineData(StackShows.Raw)]
    [InlineData(StackShows.Jpeg)]
    public void BuildingOneFileAtATimeAgreesWithArrangingTheLot(StackShows shows)
    {
        var files = new List<MediaFile>();
        for (var i = 0; i < 40; i++)
        {
            files.Add(At($"/d2/IMG_{i:0000}.CR2"));
            files.Add(At($"/d2/IMG_{i:0000}.JPG"));
            if (i % 7 == 0) { files.Add(At($"/d2/IMG_{i:0000}.DNG")); }
            if (i % 11 == 0) { files.Add(At($"/d2/CLIP_{i:0000}.mov")); }
        }

        var shuffled = files.OrderBy(f => f.FileName.GetHashCode(StringComparison.Ordinal)).ToList();

        // The same state machine the grid runs: a face per key, replaced when a better one turns up.
        var faces = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shown = new List<MediaFile>();

        foreach (var file in shuffled)
        {
            if (MediaStacking.KeyOf(file) is not { } key)
            {
                shown.Add(file);
                continue;
            }

            if (!faces.TryGetValue(key, out var slot))
            {
                faces[key] = shown.Count;
                shown.Add(file);
            }
            else if (MediaStacking.IsBetterFace(file, shown[slot], shows))
            {
                shown[slot] = file;
            }
        }

        Assert.Equal(
            MediaStacking.Arrange(shuffled, shows).Select(g => g.Face.FullPath).ToArray(),
            shown.Select(f => f.FullPath).ToArray());
    }
}
