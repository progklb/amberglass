using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.Database;
using Amberglass.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void An_empty_query_is_empty()
    {
        Assert.True(SearchQueryParser.Parse(null).IsEmpty);
        Assert.True(SearchQueryParser.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Bare_words_become_free_text()
    {
        var query = SearchQueryParser.Parse("lioness dawn");

        Assert.Equal(["lioness", "dawn"], query.FreeText.ToArray());
    }

    [Fact]
    public void Keyword_filters_are_recognised()
    {
        var query = SearchQueryParser.Parse("keyword:motorcycle kw:travel");

        Assert.Equal([["motorcycle"], ["travel"]], query.Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
        Assert.Empty(query.FreeText);
    }

    [Theory]
    [InlineData("rating:>=4", ComparisonOperator.GreaterThanOrEqual, 4)]
    [InlineData("rating:<=2", ComparisonOperator.LessThanOrEqual, 2)]
    [InlineData("rating:>3", ComparisonOperator.GreaterThan, 3)]
    [InlineData("rating:<1", ComparisonOperator.LessThan, 1)]
    [InlineData("rating:5", ComparisonOperator.Equal, 5)]
    public void Rating_comparisons_are_parsed(string text, ComparisonOperator op, int value)
    {
        var rating = SearchQueryParser.Parse(text).Rating;

        Assert.NotNull(rating);
        Assert.Equal(op, rating.Operator);
        Assert.Equal(value, rating.Value);
    }

    [Theory]
    [InlineData("rating:9")]
    [InlineData("rating:abc")]
    [InlineData("rating:")]
    public void A_nonsense_rating_is_reported_rather_than_ignored(string text)
    {
        var query = SearchQueryParser.Parse(text);

        // Silently dropping a filter would return results the user did not ask for.
        Assert.Null(query.Rating);
        Assert.NotEmpty(query.UnrecognisedTerms);
    }

    [Theory]
    [InlineData("type:video", new[] { MediaKind.Video })]
    [InlineData("type:videos", new[] { MediaKind.Video })]
    [InlineData("type:raw", new[] { MediaKind.Raw })]
    [InlineData("type:jpg", new[] { MediaKind.Jpeg })]
    // "image" is every still, raw or not, which is what it meant before raw became separable.
    [InlineData("type:image", new[] { MediaKind.Raw, MediaKind.Jpeg })]
    [InlineData("type:photos", new[] { MediaKind.Raw, MediaKind.Jpeg })]
    [InlineData("type:raw,video", new[] { MediaKind.Raw, MediaKind.Video })]
    public void Media_kind_filters_are_parsed(string text, MediaKind[] expected)
        => Assert.Equal(expected, SearchQueryParser.Parse(text).Kinds.ToArray());

    [Fact]
    public void An_unknown_media_type_is_reported()
    {
        var query = SearchQueryParser.Parse("type:audio");

        Assert.Empty(query.Kinds);
        Assert.Contains("type:audio", query.UnrecognisedTerms);
    }

    [Fact]
    public void Quoted_values_keep_their_spaces()
    {
        var query = SearchQueryParser.Parse("lens:\"RF 100-500\"");

        Assert.Equal(["RF 100-500"], query.Lenses.ToArray());
    }

    [Fact]
    public void Filters_combine()
    {
        var query = SearchQueryParser.Parse("rating:>=4 AND keyword:motorcycle AND type:video");

        Assert.Equal(4, query.Rating!.Value);
        Assert.Equal([["motorcycle"]], query.Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
        Assert.Equal([MediaKind.Video], query.Kinds.ToArray());

        // A literal AND is accepted but is not itself a search term.
        Assert.Empty(query.FreeText);
    }

    [Fact]
    public void Camera_filters_are_parsed()
        => Assert.Equal(["Sony"], SearchQueryParser.Parse("camera:Sony").Cameras.ToArray());

    [Theory]
    [InlineData("date:>=2024-01-01", ComparisonOperator.GreaterThanOrEqual)]
    [InlineData("date:<2020-06-15", ComparisonOperator.LessThan)]
    public void Date_comparisons_are_parsed(string text, ComparisonOperator op)
    {
        var date = SearchQueryParser.Parse(text).CaptureDate;

        Assert.NotNull(date);
        Assert.Equal(op, date.Operator);
    }

    [Fact]
    public void A_bare_year_means_from_the_start_of_that_year()
    {
        var date = SearchQueryParser.Parse("date:2024").CaptureDate;

        Assert.NotNull(date);
        Assert.Equal(2024, date.Value.Year);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, date.Operator);
    }

    [Fact]
    public void An_unknown_field_is_treated_as_text_rather_than_discarded()
    {
        // Someone searching for a URL should still get results.
        var query = SearchQueryParser.Parse("http://example.com");

        Assert.Contains("http://example.com", query.FreeText);
    }

    [Fact]
    public void Tokenizing_respects_quotes()
        => Assert.Equal(
            ["lens:\"RF 100-500\"", "rating:>=4"],
            SearchQueryParser.Tokenize("lens:\"RF 100-500\" rating:>=4").ToArray());
}

public class CatalogQueryBuilderTests
{
    [Fact]
    public void Free_text_becomes_a_prefix_match()
    {
        // "namib" should find "Namibia", so every term gets a trailing wildcard.
        Assert.Equal("\"namib\"*", SqliteCatalog.BuildMatchExpression(["namib"]));
    }

    [Fact]
    public void Multiple_terms_are_anded()
        => Assert.Equal("\"a\"* AND \"b\"*", SqliteCatalog.BuildMatchExpression(["a", "b"]));

    [Fact]
    public void Quotes_in_a_term_cannot_break_out_of_the_expression()
    {
        // A stray quote would otherwise turn into FTS syntax and throw.
        Assert.Equal("\"ab\"*", SqliteCatalog.BuildMatchExpression(["a\"b"]));
    }

    [Fact]
    public void Every_value_is_parameterised()
    {
        var query = SearchQueryParser.Parse("keyword:motorcycle camera:Sony rating:>=4 type:video lioness");
        var (sql, parameters) = SqliteCatalog.BuildSearch(query, null, 100);

        // A search box is user input; nothing from it may be concatenated into SQL.
        Assert.DoesNotContain("motorcycle", sql);
        Assert.DoesNotContain("Sony", sql);
        Assert.DoesNotContain("lioness", sql);
        Assert.Contains("@keyword0", sql);
        Assert.Contains("@camera0", sql);
        Assert.Contains("@rating", sql);
        // keyword0_0 rather than keyword0: a keyword filter is now a group of alternatives, and
        // each word in it gets its own parameter.
        Assert.Contains("keyword0_0", parameters.ParameterNames);
    }
}

public class CaptureDateParsingTests
{
    [Fact]
    public void Exif_style_dates_are_understood()
    {
        // EXIF uses colons between date parts, which no standard parser accepts as-is.
        var parsed = CatalogIndexer.ParseCaptureDate("2024:06:01 09:15:22");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2024, 6, 1, 9, 15, 22), parsed.Value.DateTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Unparseable_values_yield_null(string? value)
        => Assert.Null(CatalogIndexer.ParseCaptureDate(value));
}

public class SqliteCatalogTests
{
    private static SqliteCatalog Create(TempFolder temp)
        => new(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

    private static CatalogEntry Entry(
        string path,
        MediaType type = MediaType.Image,
        string? title = null,
        string? description = null,
        int? rating = null,
        string? camera = null,
        string? lens = null,
        DateTimeOffset? captureDate = null,
        params string[] keywords)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = type,
                SizeBytes = 100,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata
            {
                Title = title,
                Description = description,
                Rating = rating,
                Keywords = [.. keywords]
            },
            new CameraInfo { Camera = camera, Lens = lens },
            HasSidecar: false,
            captureDate);

    /// <summary>
    /// Search results are tiles too, built from the catalog rather than a scan, so the catalog's
    /// record of the sidecar has to come back with them.
    /// </summary>
    [Fact]
    public async Task A_search_hit_says_whether_the_file_has_a_sidecar()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/with.jpg", title: "Coast") with { HasSidecar = true },
            Entry("/without.jpg", title: "Coast")
        ]);

        var hits = await catalog.SearchAsync(SearchQueryParser.Parse("Coast"));

        Assert.True(hits.Single(h => h.FullPath == "/with.jpg").ToMediaFile().HasSidecar);
        Assert.False(hits.Single(h => h.FullPath == "/without.jpg").ToMediaFile().HasSidecar);
    }

    private static async Task<IReadOnlyList<string>> SearchPathsAsync(SqliteCatalog catalog, string query)
        => (await catalog.SearchAsync(SearchQueryParser.Parse(query))).Select(h => h.FullPath).ToList();

    [Fact]
    public async Task An_empty_catalog_reports_nothing()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        var stats = await catalog.GetStatisticsAsync();

        Assert.Equal(0, stats.FileCount);
    }

    [Fact]
    public async Task Entries_can_be_stored_and_counted()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["wildlife", "namibia"]),
            Entry("/b.jpg", keywords: ["wildlife"])
        ]);

        var stats = await catalog.GetStatisticsAsync();

        Assert.Equal(2, stats.FileCount);
        Assert.Equal(2, stats.KeywordCount);
    }

    [Fact]
    public async Task Re_indexing_a_file_updates_rather_than_duplicating_it()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/a.jpg", title: "First")]);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Second")]);

        var stats = await catalog.GetStatisticsAsync();
        Assert.Equal(1, stats.FileCount);

        var hits = await catalog.SearchAsync(SearchQueryParser.Parse("Second"));
        Assert.Single(hits);
        Assert.Equal("Second", hits[0].Title);

        // The old text must no longer match, or the index has drifted from the data.
        Assert.Empty(await catalog.SearchAsync(SearchQueryParser.Parse("First")));
    }

    [Fact]
    public async Task Free_text_searches_titles_and_descriptions()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", title: "Lioness at dawn"),
            Entry("/b.jpg", description: "Motorcycle on a gravel road"),
            Entry("/c.jpg", title: "Something else")
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "lioness"));
        Assert.Equal(["/b.jpg"], await SearchPathsAsync(catalog, "gravel"));
    }

    [Fact]
    public async Task Free_text_matches_on_a_prefix()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Namibia trip")]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "namib"));
    }

    [Fact]
    public async Task Free_text_finds_keywords_too()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["motorcycle"])]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "motorcycle"));
    }

    [Fact]
    public async Task Keyword_filters_match_exactly_and_case_insensitively()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["Motorcycle", "Travel"]),
            Entry("/b.jpg", keywords: ["Travel"])
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "keyword:motorcycle"));
        Assert.Equal(["/a.jpg", "/b.jpg"], (await SearchPathsAsync(catalog, "keyword:TRAVEL")).Order().ToArray());
    }

    [Fact]
    public async Task Multiple_keywords_must_all_be_present()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["motorcycle", "travel"]),
            Entry("/b.jpg", keywords: ["motorcycle"])
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "keyword:motorcycle keyword:travel"));
    }

    [Fact]
    public async Task Rating_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/one.jpg", rating: 1),
            Entry("/four.jpg", rating: 4),
            Entry("/five.jpg", rating: 5),
            Entry("/none.jpg")
        ]);

        var atLeastFour = await SearchPathsAsync(catalog, "rating:>=4");

        Assert.Equal(["/five.jpg", "/four.jpg"], atLeastFour.Order().ToArray());

        // An unrated file must not satisfy a rating filter.
        Assert.DoesNotContain("/none.jpg", atLeastFour);
    }

    [Fact]
    public async Task Media_type_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg"),
            Entry("/b.mp4", MediaType.Video)
        ]);

        Assert.Equal(["/b.mp4"], await SearchPathsAsync(catalog, "type:video"));
        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "type:image"));
    }

    [Fact]
    public async Task Camera_and_lens_filters_match_partially()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", camera: "Sony A7 IV", lens: "FE 24-70mm F2.8"),
            Entry("/b.jpg", camera: "Canon EOS R5", lens: "RF 100-500mm")
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "camera:Sony"));
        Assert.Equal(["/b.jpg"], await SearchPathsAsync(catalog, "lens:\"RF 100-500\""));
    }

    [Fact]
    public async Task Date_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/old.jpg", captureDate: new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("/new.jpg", captureDate: new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero))
        ]);

        Assert.Equal(["/new.jpg"], await SearchPathsAsync(catalog, "date:>=2024-01-01"));
        Assert.Equal(["/old.jpg"], await SearchPathsAsync(catalog, "date:<2020-01-01"));
    }

    [Fact]
    public async Task Filters_combine_with_and_semantics()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/hit.mp4", MediaType.Video, rating: 5, keywords: ["motorcycle"]),
            Entry("/wrongtype.jpg", MediaType.Image, rating: 5, keywords: ["motorcycle"]),
            Entry("/wrongrating.mp4", MediaType.Video, rating: 2, keywords: ["motorcycle"]),
            Entry("/wrongkeyword.mp4", MediaType.Video, rating: 5, keywords: ["travel"])
        ]);

        Assert.Equal(
            ["/hit.mp4"],
            await SearchPathsAsync(catalog, "rating:>=4 AND keyword:motorcycle AND type:video"));
    }

    [Fact]
    public async Task Removed_keywords_stop_matching_after_a_re_index()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["wildlife", "namibia"])]);
        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["wildlife"])]);

        Assert.Empty(await SearchPathsAsync(catalog, "keyword:namibia"));
        Assert.Single(await SearchPathsAsync(catalog, "keyword:wildlife"));
    }

    [Fact]
    public async Task Entries_for_deleted_files_can_be_pruned()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        var present = Path.Combine(temp.Path, "present.jpg");
        await File.WriteAllTextAsync(present, "x");

        await catalog.UpsertAsync([Entry(present), Entry(Path.Combine(temp.Path, "gone.jpg"))]);

        var removed = await catalog.RemoveMissingAsync();

        Assert.Equal(1, removed);
        Assert.Equal(1, (await catalog.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task Clearing_empties_the_catalog()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Something", keywords: ["kw"])]);

        await catalog.ClearAsync();

        Assert.Equal(0, (await catalog.GetStatisticsAsync()).FileCount);
        Assert.Empty(await SearchPathsAsync(catalog, "Something"));
    }

    [Fact]
    public async Task The_catalog_survives_being_reopened()
    {
        using var temp = new TempFolder();

        await Create(temp).UpsertAsync([Entry("/a.jpg", title: "Persisted")]);

        // A new instance stands in for the next launch.
        Assert.Single(await Create(temp).SearchAsync(SearchQueryParser.Parse("Persisted")));
    }

    [Fact]
    public async Task A_query_that_matches_nothing_returns_nothing_rather_than_everything()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Lioness")]);

        Assert.Empty(await SearchPathsAsync(catalog, "keyword:doesnotexist"));
    }
}

public class CatalogManagementTests
{
    /// <summary>Lets the catalog path be moved at runtime, as the settings UI does.</summary>
    private sealed class MovablePaths(string root) : IAppPaths
    {
        public string AppDataRoot { get; } = root;
        public string CacheRoot => Path.Combine(AppDataRoot, "Cache");
        public string DefaultCacheRoot => Path.Combine(AppDataRoot, "Cache");
        public string ThumbnailCacheRoot => Path.Combine(CacheRoot, "Thumbnails");
        public string VideoProxyCacheRoot => Path.Combine(CacheRoot, "VideoProxy");

        public string RenderCacheRoot => Path.Combine(CacheRoot, "Renders");
        public string LogRoot => Path.Combine(AppDataRoot, "Logs");
        public string DefaultCatalogPath => Path.Combine(AppDataRoot, "catalog.db");
        public string? Override { get; set; }
        public string CatalogPath => Override is null ? DefaultCatalogPath : Path.Combine(Override, "catalog.db");
    }

    private static CatalogEntry Entry(string path)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = MediaType.Image,
                SizeBytes = 10,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata { Title = "Indexed", Keywords = ["kw"] },
            CameraInfo.Empty,
            HasSidecar: false,
            null);

    [Fact]
    public async Task Statistics_report_the_size_on_disk()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([Entry("/a.jpg")]);

        var stats = await catalog.GetStatisticsAsync();

        // Includes the WAL, which in this state is usually larger than the database itself.
        Assert.True(stats.SizeBytes > 0, "catalog reported zero bytes on disk");
    }

    [Fact]
    public async Task Moving_the_catalog_starts_a_fresh_one_and_leaves_the_old_file_alone()
    {
        using var original = new TempFolder();
        using var moved = new TempFolder();

        var paths = new MovablePaths(original.Path);
        var catalog = new SqliteCatalog(paths, NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([Entry("/a.jpg"), Entry("/b.jpg")]);
        Assert.Equal(2, (await catalog.GetStatisticsAsync()).FileCount);

        // Relocating must take effect without recreating the service.
        paths.Override = moved.Path;

        Assert.Equal(0, (await catalog.GetStatisticsAsync()).FileCount);
        Assert.True(File.Exists(Path.Combine(moved.Path, "catalog.db")));

        // The old catalog is still intact where it was.
        paths.Override = null;
        Assert.Equal(2, (await catalog.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task The_relocated_catalog_is_usable_immediately()
    {
        using var original = new TempFolder();
        using var moved = new TempFolder();

        var paths = new MovablePaths(original.Path) { Override = moved.Path };
        var catalog = new SqliteCatalog(paths, NullLogger<SqliteCatalog>.Instance);

        // Schema must be applied at the new location, not assumed from the old one.
        await catalog.UpsertAsync([Entry("/new.jpg")]);

        Assert.Single(await catalog.SearchAsync(SearchQueryParser.Parse("Indexed")));
    }

    [Fact]
    public async Task Clearing_reclaims_disk_space()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync(Enumerable.Range(0, 400).Select(i => Entry($"/f{i}.jpg")).ToList());
        var before = (await catalog.GetStatisticsAsync()).SizeBytes;

        await catalog.ClearAsync();
        var after = (await catalog.GetStatisticsAsync()).SizeBytes;

        // Deleting rows alone would leave the file the same size, making "Clear" look broken.
        Assert.True(after < before, $"expected the file to shrink; was {before}, now {after}");
        Assert.Equal(0, (await catalog.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task Pruning_removes_only_entries_whose_files_are_gone()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        var present = Path.Combine(temp.Path, "present.jpg");
        await File.WriteAllTextAsync(present, "x");

        await catalog.UpsertAsync([
            Entry(present),
            Entry(Path.Combine(temp.Path, "deleted-one.jpg")),
            Entry(Path.Combine(temp.Path, "deleted-two.jpg"))
        ]);

        var removed = await catalog.RemoveMissingAsync();

        Assert.Equal(2, removed);

        var remaining = await catalog.SearchAsync(SearchQueryParser.Parse("Indexed"));
        Assert.Equal([present], remaining.Select(h => h.FullPath).ToArray());
    }

    [Fact]
    public async Task Pruning_an_intact_catalog_removes_nothing()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        var present = Path.Combine(temp.Path, "present.jpg");
        await File.WriteAllTextAsync(present, "x");
        await catalog.UpsertAsync([Entry(present)]);

        Assert.Equal(0, await catalog.RemoveMissingAsync());
        Assert.Equal(1, (await catalog.GetStatisticsAsync()).FileCount);
    }
}

public class WorkspaceScopeTests
{
    private static CatalogEntry Entry(string path)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = MediaType.Image,
                SizeBytes = 10,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata { Title = "Lioness at dawn" },
            CameraInfo.Empty,
            HasSidecar: false,
            null);

    private static string Sep(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void A_root_without_a_trailing_separator_gets_one()
    {
        // Without it, a root of /photos/nam would also match /photos/namibia.
        var normalised = SqliteCatalog.NormaliseRoot(Sep("/photos/nam"));

        Assert.Equal(Sep("/photos/nam") + Path.DirectorySeparatorChar, normalised);
    }

    [Fact]
    public void A_root_that_already_ends_in_a_separator_is_left_alone()
    {
        var root = Sep("/photos/nam") + Path.DirectorySeparatorChar;

        Assert.Equal(root, SqliteCatalog.NormaliseRoot(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_root_means_no_scoping(string? root)
        => Assert.Null(SqliteCatalog.NormaliseRoot(root));

    [Fact]
    public void The_scope_is_parameterised()
    {
        var (sql, parameters) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("lioness"), "/photos", 100);

        Assert.DoesNotContain("/photos", sql);
        Assert.Contains("root", parameters.ParameterNames);
    }

    [Fact]
    public async Task Search_is_limited_to_the_workspace()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry(Sep("/photos/namibia/a.jpg")),
            Entry(Sep("/photos/namibia/deeper/b.jpg")),
            Entry(Sep("/photos/kenya/c.jpg"))
        ]);

        var query = SearchQueryParser.Parse("lioness");

        var scoped = await catalog.SearchAsync(query, Sep("/photos/namibia"));
        var everywhere = await catalog.SearchAsync(query);

        Assert.Equal(2, scoped.Count);
        Assert.All(scoped, hit => Assert.Contains("namibia", hit.FullPath));
        Assert.Equal(3, everywhere.Count);
    }

    [Fact]
    public async Task A_sibling_folder_with_a_shared_prefix_is_not_included()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry(Sep("/photos/nam/a.jpg")),
            Entry(Sep("/photos/namibia/b.jpg"))
        ]);

        var hits = await catalog.SearchAsync(SearchQueryParser.Parse("lioness"), Sep("/photos/nam"));

        // The classic prefix-matching bug: /photos/nam must not swallow /photos/namibia.
        Assert.Equal([Sep("/photos/nam/a.jpg")], hits.Select(h => h.FullPath).ToArray());
    }

    [Fact]
    public async Task A_path_containing_a_LIKE_wildcard_is_matched_literally()
    {
        using var temp = new TempFolder();
        var catalog = new SqliteCatalog(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

        await catalog.UpsertAsync([
            Entry(Sep("/photos/100%/a.jpg")),
            Entry(Sep("/photos/100x/b.jpg"))
        ]);

        // Under LIKE, "%" would be a wildcard and would also match /photos/100x.
        var hits = await catalog.SearchAsync(SearchQueryParser.Parse("lioness"), Sep("/photos/100%"));

        Assert.Equal([Sep("/photos/100%/a.jpg")], hits.Select(h => h.FullPath).ToArray());
    }
}

public class RecentWorkspaceTests
{
    [Fact]
    public void Opening_a_workspace_puts_it_first()
    {
        var settings = AppSettings.Default.WithWorkspace("/a").WithWorkspace("/b");

        Assert.Equal("/b", settings.LastWorkspacePath);
        Assert.Equal(["/b", "/a"], settings.RecentWorkspaces.ToArray());
    }

    [Fact]
    public void Reopening_moves_it_up_rather_than_duplicating()
    {
        var settings = AppSettings.Default
            .WithWorkspace("/a")
            .WithWorkspace("/b")
            .WithWorkspace("/a");

        Assert.Equal(["/a", "/b"], settings.RecentWorkspaces.ToArray());
    }

    [Fact]
    public void The_list_is_capped()
    {
        var settings = AppSettings.Default;
        for (var i = 0; i < AppSettings.MaxRecentWorkspaces + 5; i++)
        {
            settings = settings.WithWorkspace($"/w{i}");
        }

        Assert.Equal(AppSettings.MaxRecentWorkspaces, settings.RecentWorkspaces.Count);

        // The oldest are the ones dropped.
        Assert.Equal($"/w{AppSettings.MaxRecentWorkspaces + 4}", settings.RecentWorkspaces[0]);
        Assert.DoesNotContain("/w0", settings.RecentWorkspaces);
    }
}

public class WorkspaceLabelTests
{
    [Fact]
    public void The_name_leads_and_the_parent_follows()
    {
        var label = WorkspaceLabel.ForMenu(Path.Combine(Path.DirectorySeparatorChar + "media", "namibia"));

        Assert.StartsWith("namibia", label);
        Assert.Contains("media", label);
    }

    [Fact]
    public void The_home_directory_is_abbreviated()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return;
        }

        var abbreviated = WorkspaceLabel.Abbreviate(Path.Combine(home, "Pictures"));

        Assert.StartsWith("~", abbreviated);
        Assert.DoesNotContain(home, abbreviated);
    }

    [Fact]
    public void A_long_path_keeps_its_tail()
    {
        var deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("a-long-folder-name", 8));

        var abbreviated = WorkspaceLabel.Abbreviate(deep);

        // The tail identifies the folder, so the front is what gets dropped.
        Assert.StartsWith("…", abbreviated);
        Assert.EndsWith("a-long-folder-name", abbreviated);
        Assert.True(abbreviated.Length <= 46, $"still {abbreviated.Length} characters");
    }
}

/// <summary>
/// The keyword query behind "Import from workspace". Exercised against a real SQLite catalog because
/// the first version passed its unit tests and then failed on contact with the database: SQLite
/// returns COUNT(*) as Int64, which Dapper would not bind to a record taking an int.
/// </summary>
public class CatalogKeywordTests
{
    private static SqliteCatalog Create(TempFolder temp)
        => new(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

    private static CatalogEntry Entry(string path, params string[] keywords)
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
            new EditableMetadata { Keywords = [.. keywords] },
            CameraInfo.Empty,
            HasSidecar: false,
            null);

    [Fact]
    public async Task Returns_distinct_keywords_with_their_counts()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/library/a.jpg", "animal", "warm"),
            Entry("/library/b.jpg", "animal"),
            Entry("/library/c.jpg", "plant")
        ]);

        var keywords = await catalog.GetKeywordsAsync();

        Assert.Equal(3, keywords.Count);

        // Most used first: that ordering is what makes a long vocabulary usable.
        Assert.Equal("animal", keywords[0].Value);
        Assert.Equal(2, keywords[0].Count);
    }

    [Fact]
    public async Task Scopes_to_a_workspace()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry(Path.Combine(temp.Path, "trip", "a.jpg"), "animal"),
            Entry(Path.Combine(temp.Path, "other", "b.jpg"), "architecture")
        ]);

        var keywords = await catalog.GetKeywordsAsync(Path.Combine(temp.Path, "trip"));

        Assert.Equal("animal", Assert.Single(keywords).Value);
    }

    /// <summary>
    /// The prefix must not run past a folder boundary — the same trap the search scoping avoids, and
    /// worth pinning here too since this query builds its own WHERE clause.
    /// </summary>
    [Fact]
    public async Task A_root_does_not_match_a_longer_sibling_folder()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry(Path.Combine(temp.Path, "nam", "a.jpg"), "wanted"),
            Entry(Path.Combine(temp.Path, "namibia", "b.jpg"), "unwanted")
        ]);

        var keywords = await catalog.GetKeywordsAsync(Path.Combine(temp.Path, "nam"));

        Assert.Equal("wanted", Assert.Single(keywords).Value);
    }

    [Fact]
    public async Task An_empty_catalog_returns_nothing()
    {
        using var temp = new TempFolder();

        Assert.Empty(await Create(temp).GetKeywordsAsync());
    }

    /// <summary>The whole point: what comes out can be turned straight into a library.</summary>
    [Fact]
    public async Task The_result_builds_a_library()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/library/a.jpg", "Subject|animal", "Mood|warm")]);

        var library = KeywordLibrary.FromFlat((await catalog.GetKeywordsAsync()).Select(k => k.Value));

        Assert.Equal(2, library.Roots.Length);
        Assert.Equal("animal", library.Roots.Single(r => r.Name == "Subject").Children.Single().Name);
    }
}

/// <summary>The SQL the new filters build, checked without needing a database.</summary>
public class KindAndKeywordSqlTests
{
    [Fact]
    public void AlternativeKeywordsBecomeOneExistsWithIn()
    {
        var (sql, parameters) = SqliteCatalog.BuildSearch(
            SearchQueryParser.Parse("k:sand,dust"), null, 100);

        // One EXISTS, two parameters inside an IN: any of them satisfies the filter.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "EXISTS"));
        Assert.Contains("@keyword0_0, @keyword0_1", sql);
        Assert.Contains("keyword0_0", parameters.ParameterNames);
        Assert.Contains("keyword0_1", parameters.ParameterNames);

        Assert.DoesNotContain("sand", sql);
        Assert.DoesNotContain("dust", sql);
    }

    [Fact]
    public void RepeatedKeywordsBecomeSeveralExists()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("k:sand k:dust"), null, 100);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "EXISTS").Count);
    }

    /// <summary>
    /// Raw has no column of its own, so it is drawn from the extension — and the list must be the
    /// registry's, not a second copy that could fall behind it.
    /// </summary>
    [Fact]
    public void RawIsFilteredByTheRegistrysExtensions()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("t:raw"), null, 100);

        Assert.All(MediaTypeRegistry.RawFileExtensions, extension =>
            Assert.Contains(extension.ToLowerInvariant(), sql));

        Assert.DoesNotContain("NOT (", sql);
    }

    [Fact]
    public void JpegIsEverythingThatIsNotRaw()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("t:jpg"), null, 100);

        Assert.Contains("NOT (", sql);
    }

    [Fact]
    public void SeveralKindsAreOredTogether()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("t:raw,video"), null, 100);

        Assert.Contains(" OR ", sql);
    }
}

/// <summary>Filename searching, which was missing entirely.</summary>
public class FileNameSearchTests
{
    [Fact]
    public void A_filename_field_matches_part_of_the_name()
    {
        var (sql, parameters) = SqliteCatalog.BuildSearch(
            SearchQueryParser.Parse("fn:DSCF7676"), null, 100);

        Assert.Contains("m.FileName LIKE @fileName0", sql);
        Assert.Contains("fileName0", parameters.ParameterNames);

        // Wrapped in wildcards rather than anchored: people remember a fragment, not the whole name.
        Assert.DoesNotContain("DSCF7676", sql);
    }

    [Fact]
    public void The_short_form_is_fn()
    {
        Assert.Equal(["DSCF7676"], SearchQueryParser.Parse("fn:DSCF7676").FileNames.ToArray());
        Assert.Equal(["DSCF7676"], SearchQueryParser.Parse("filename:DSCF7676").FileNames.ToArray());
    }

    /// <summary>
    /// Bare words go to the full-text index, which now covers the filename — so typing a filename
    /// into the box finds the file without knowing any syntax at all. That was the actual complaint.
    /// </summary>
    [Fact]
    public void Free_text_reaches_the_search_index()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("DSCF7676"), null, 100);

        Assert.Contains("MediaSearch MATCH", sql);
    }
}

/// <summary>The cull flag in SQL, including the one value that is not a simple equality.</summary>
public class FlagSearchTests
{
    [Fact]
    public void A_flag_is_matched_by_value()
    {
        var (sql, parameters) = SqliteCatalog.BuildSearch(
            SearchQueryParser.Parse("f:rejected"), null, 100);

        Assert.Contains("m.Flag = @flag0", sql);
        Assert.Contains("flag0", parameters.ParameterNames);
    }

    /// <summary>
    /// "None" has to include rows with no flag at all as well as rows explicitly cleared. A plain
    /// equality would miss every file nobody has judged yet, which is the whole point of asking.
    /// </summary>
    [Fact]
    public void Unflagged_includes_rows_that_were_never_flagged()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(SearchQueryParser.Parse("f:none"), null, 100);

        Assert.Contains("m.Flag IS NULL", sql);
    }

    [Fact]
    public void Several_flags_are_ored_together()
    {
        var (sql, _) = SqliteCatalog.BuildSearch(
            SearchQueryParser.Parse("f:accepted,rejected"), null, 100);

        Assert.Contains(" OR ", sql);
    }
}
