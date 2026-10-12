using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Reading a whole folder's ratings, flags and labels in one query — what the grid draws on its
/// tiles. Per-file lookups would be thousands of round trips to say "nothing" about most of them.
/// </summary>
public class MediaMarksTests
{
    private static SqliteCatalog Create(TempFolder temp)
        => new(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

    private static CatalogEntry Entry(
        string path, int? rating = null, MediaFlag? flag = null, string? label = null)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = MediaType.Image,
                SizeBytes = 100,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata { Rating = rating, Flag = flag, Label = label },
            new CameraInfo(),
            HasSidecar: false,
            null);

    [Fact]
    public async Task EachMarkComesBackAgainstItsFile()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/w/a.raf", rating: 4),
            Entry("/w/b.raf", flag: MediaFlag.Rejected),
            Entry("/w/c.raf", label: "Yellow")
        ]);

        var marks = await catalog.GetMarksAsync();

        Assert.Equal(4, marks["/w/a.raf"].Rating);
        Assert.Equal(MediaFlag.Rejected, marks["/w/b.raf"].Flag);
        Assert.Equal("Yellow", marks["/w/c.raf"].Label);
    }

    [Fact]
    public async Task AllThreeTogether()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/w/a.raf", rating: 5, flag: MediaFlag.Accepted, label: "Select")]);

        var marks = await catalog.GetMarksAsync();

        Assert.Equal(new MediaMarks(5, MediaFlag.Accepted, "Select"), marks["/w/a.raf"]);
    }

    /// <summary>
    /// The saving that makes this worth doing as one query: most files in most folders have nothing
    /// to say, and a tile with no marks needs no row.
    /// </summary>
    [Fact]
    public async Task FilesWithNothingToSayAreNotReturned()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/w/plain.raf"),
            Entry("/w/rated.raf", rating: 3)
        ]);

        var marks = await catalog.GetMarksAsync();

        Assert.Single(marks);
        Assert.False(marks.ContainsKey("/w/plain.raf"));
    }

    [Fact]
    public async Task TheScopeIsTheFolderAsked()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry(Path.Combine(temp.Path, "here", "a.raf"), rating: 4),
            Entry(Path.Combine(temp.Path, "elsewhere", "b.raf"), rating: 5)
        ]);

        var marks = await catalog.GetMarksAsync(Path.Combine(temp.Path, "here"));

        Assert.Single(marks);
        Assert.Equal(4, marks.Values.Single().Rating);
    }

    /// <summary>Subfolders are included: the grid shows them when "include subfolders" is on.</summary>
    [Fact]
    public async Task TheScopeReachesIntoSubfolders()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry(Path.Combine(temp.Path, "here", "deep", "a.raf"), rating: 2)]);

        Assert.Single(await catalog.GetMarksAsync(Path.Combine(temp.Path, "here")));
    }

    [Fact]
    public async Task AnEmptyCatalogSaysNothingRatherThanFailing()
    {
        using var temp = new TempFolder();

        Assert.Empty(await Create(temp).GetMarksAsync());
    }
}
