using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// Which way round a picture is — which is not what its width and height say.
/// </summary>
public class OrientationTests
{
    [Fact]
    public void WiderThanTallIsLandscape()
        => Assert.Equal(MediaOrientation.Landscape, new ImageDimensions(6240, 4160).Orientation);

    [Fact]
    public void TallerThanWideIsPortrait()
        => Assert.Equal(MediaOrientation.Portrait, new ImageDimensions(4160, 6240).Orientation);

    [Fact]
    public void EqualIsSquare()
        => Assert.Equal(MediaOrientation.Square, new ImageDimensions(4000, 4000).Orientation);

    /// <summary>
    /// The case the whole thing exists for. A real file from this workspace: 6240x4160 stored, which
    /// reads as landscape, and "Rotate 270 CW" — so every viewer shows it as a portrait, and so must
    /// a search for one.
    /// </summary>
    [Fact]
    public void ACameraHeldOnItsSideIsAPortrait()
    {
        var dimensions = ImageDimensions.From(6240, 4160, "Rotate 270 CW");

        Assert.Equal(MediaOrientation.Portrait, dimensions!.Value.Orientation);
        Assert.Equal(4160, dimensions.Value.Width);
        Assert.Equal(6240, dimensions.Value.Height);
    }

    /// <summary>All four quarter-turn orientations, including the mirrored ones.</summary>
    [Theory]
    [InlineData("Rotate 90 CW")]
    [InlineData("Rotate 270 CW")]
    [InlineData("Mirror horizontal and rotate 90 CW")]
    [InlineData("Mirror horizontal and rotate 270 CW")]
    public void AQuarterTurnExchangesTheAxes(string orientation)
        => Assert.Equal(
            MediaOrientation.Portrait,
            ImageDimensions.From(6240, 4160, orientation)!.Value.Orientation);

    /// <summary>The four that do not turn it. "Rotate 180" is the one worth naming.</summary>
    [Theory]
    [InlineData("Horizontal (normal)")]
    [InlineData("Mirror horizontal")]
    [InlineData("Rotate 180")]
    [InlineData("Mirror vertical")]
    public void TheOtherOrientationsLeaveItAlone(string orientation)
        => Assert.Equal(
            MediaOrientation.Landscape,
            ImageDimensions.From(6240, 4160, orientation)!.Value.Orientation);

    [Fact]
    public void NoOrientationTagMeansTakeTheNumbersAsTheyAre()
        => Assert.Equal(
            MediaOrientation.Landscape,
            ImageDimensions.From(6240, 4160, null)!.Value.Orientation);

    [Theory]
    [InlineData(null, 100)]
    [InlineData(100, null)]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    public void AMissingOrNonsensicalMeasurementIsNoAnswer(int? width, int? height)
        => Assert.Null(ImageDimensions.From(width, height, null));
}

/// <summary>
/// The search term, from what is typed through to what the catalog is asked.
/// </summary>
public class OrientationSearchTests
{
    private static SearchQuery Parse(string text) => SearchQueryParser.Parse(text);

    [Fact]
    public void TheShortFormAndTheLongOneAreTheSame()
        // .ToArray() on both sides: xUnit compares ImmutableArray by reference and reports
        // "collections differ" over two identical lists.
        => Assert.Equal(
            Parse("orientation:portrait").Orientations.ToArray(),
            Parse("o:portrait").Orientations.ToArray());

    [Theory]
    [InlineData("o:portrait", MediaOrientation.Portrait)]
    [InlineData("o:landscape", MediaOrientation.Landscape)]
    [InlineData("o:square", MediaOrientation.Square)]
    public void EachShapeParses(string query, MediaOrientation expected)
        => Assert.Equal(expected, Assert.Single(Parse(query).Orientations));

    /// <summary>Words a photographer might reach for instead.</summary>
    [Theory]
    [InlineData("o:vertical", MediaOrientation.Portrait)]
    [InlineData("o:tall", MediaOrientation.Portrait)]
    [InlineData("o:horizontal", MediaOrientation.Landscape)]
    [InlineData("o:wide", MediaOrientation.Landscape)]
    public void TheOtherWordsForItWork(string query, MediaOrientation expected)
        => Assert.Equal(expected, Assert.Single(Parse(query).Orientations));

    [Fact]
    public void CommasMeanEither()
        => Assert.Equal(2, Parse("o:portrait,square").Orientations.Length);

    [Fact]
    public void CaseDoesNotMatter()
        => Assert.Equal(MediaOrientation.Portrait, Assert.Single(Parse("O:Portrait").Orientations));

    [Fact]
    public void AWordThatIsNotAShapeIsReportedRatherThanIgnored()
    {
        var query = Parse("o:diagonal");

        Assert.Empty(query.Orientations);
        Assert.Single(query.UnrecognisedTerms);
    }

    /// <summary>It narrows alongside everything else, as every other field does.</summary>
    [Fact]
    public void ItCombinesWithOtherFilters()
    {
        var query = Parse("o:portrait r:>=4 k:sand");

        Assert.Single(query.Orientations);
        Assert.NotNull(query.Rating);
        Assert.Single(query.Keywords);
    }

    [Fact]
    public void ItIsOfferedInTheHelpAndAcceptedByTheParser()
    {
        // The catalogue drives the help, the suggestions and the parser alike; this pins that the
        // new field is in all three rather than only the first.
        var field = Assert.Single(SearchFields.All.Where(f => f.Name == "orientation"));

        Assert.Equal("o", field.Short);
        Assert.Single(Parse($"{field.Short}:{"portrait"}").Orientations);
    }
}

/// <summary>
/// The search reaching the catalog — that the stored dimensions and the SQL agree about which way
/// round a picture is.
/// </summary>
public class OrientationCatalogTests
{
    private static CatalogEntry Entry(string path, int? width, int? height, string? orientation = null)
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
            EditableMetadata.Empty,
            new CameraInfo(),
            HasSidecar: false,
            null,
            ImageDimensions.From(width, height, orientation));

    private static async Task<IReadOnlyList<string>> FindAsync(SqliteCatalog catalog, string query)
        => (await catalog.SearchAsync(SearchQueryParser.Parse(query)))
            .Select(h => h.FileName).OrderBy(n => n).ToList();

    [Fact]
    public async Task EachShapeFindsItsOwn()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry("/w/wide.jpg", 6240, 4160),
            Entry("/w/tall.jpg", 4160, 6240),
            Entry("/w/square.jpg", 4000, 4000)
        ]);

        Assert.Equal(["wide.jpg"], await FindAsync(catalog, "o:landscape"));
        Assert.Equal(["tall.jpg"], await FindAsync(catalog, "o:portrait"));
        Assert.Equal(["square.jpg"], await FindAsync(catalog, "o:square"));
    }

    /// <summary>
    /// The whole point, end to end: a file whose stored numbers say landscape and whose orientation
    /// tag says otherwise is found by a search for portraits.
    /// </summary>
    [Fact]
    public async Task ARotatedFileIsFoundAsAPortrait()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([Entry("/w/rotated.jpg", 6240, 4160, "Rotate 270 CW")]);

        Assert.Equal(["rotated.jpg"], await FindAsync(catalog, "o:portrait"));
        Assert.Empty(await FindAsync(catalog, "o:landscape"));
    }

    [Fact]
    public async Task CommasFindEither()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry("/w/wide.jpg", 6240, 4160),
            Entry("/w/tall.jpg", 4160, 6240),
            Entry("/w/square.jpg", 4000, 4000)
        ]);

        Assert.Equal(["square.jpg", "tall.jpg"], await FindAsync(catalog, "o:portrait,square"));
    }

    /// <summary>
    /// A file indexed before dimensions were recorded has none. Excluded rather than guessed at —
    /// calling it landscape because two nulls compare equal would be worse than not answering.
    /// </summary>
    [Fact]
    public async Task AFileWithNoDimensionsIsNotAnyShape()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([Entry("/w/unknown.jpg", null, null)]);

        Assert.Empty(await FindAsync(catalog, "o:portrait"));
        Assert.Empty(await FindAsync(catalog, "o:landscape"));
        Assert.Empty(await FindAsync(catalog, "o:square"));

        // Still findable by everything else, so the missing dimensions cost only this one filter.
        Assert.Single(await FindAsync(catalog, "fn:unknown"));
    }

    [Fact]
    public async Task ItNarrowsAlongsideOtherFilters()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry("/w/tall-raf.RAF", 4160, 6240),
            Entry("/w/tall-jpg.jpg", 4160, 6240),
            Entry("/w/wide-raf.RAF", 6240, 4160)
        ]);

        Assert.Equal(["tall-raf.RAF"], await FindAsync(catalog, "o:portrait t:raw"));
    }
}
