using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Cache;
using Xunit;

namespace Amberglass.Tests;

public class ThumbnailCacheTests
{

    private static MediaFile Sample(string path = "/library/IMG001.jpg", long size = 1234, long ticks = 5_000_000)
        => new()
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            MediaType = MediaType.Image,
            SizeBytes = size,
            ModifiedUtc = new DateTimeOffset(ticks, TimeSpan.Zero),
            CreatedUtc = new DateTimeOffset(ticks, TimeSpan.Zero)
        };

    [Fact]
    public void Cache_key_is_stable_for_identical_input()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        Assert.Equal(cache.GetCacheKey(Sample(), 320), cache.GetCacheKey(Sample(), 320));
    }

    [Fact]
    public void Cache_key_changes_when_the_source_file_changes()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));
        var original = cache.GetCacheKey(Sample(), 320);

        Assert.NotEqual(original, cache.GetCacheKey(Sample(size: 9999), 320));
        Assert.NotEqual(original, cache.GetCacheKey(Sample(ticks: 6_000_000), 320));
        Assert.NotEqual(original, cache.GetCacheKey(Sample(path: "/library/IMG002.jpg"), 320));
    }

    [Fact]
    public void Cache_key_changes_with_requested_size()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        Assert.NotEqual(cache.GetCacheKey(Sample(), 320), cache.GetCacheKey(Sample(), 1600));
    }

    /// <summary>
    /// A fix to RAW rendering has to invalidate RAW thumbnails, or a folder already browsed keeps
    /// serving the wrong picture for as long as the files are untouched.
    /// </summary>
    [Fact]
    public void A_raw_thumbnail_key_carries_the_renderer_version()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        var unversioned = Sha256($"/library/IMG001.CR2|1234|5000000|320");

        Assert.NotEqual(unversioned, cache.GetCacheKey(Sample(path: "/library/IMG001.CR2"), 320));
    }

    /// <summary>
    /// And must not invalidate anything else. Pinned as a literal because the cost of getting this
    /// wrong — every JPEG thumbnail in every workspace regenerated — is invisible in a passing suite
    /// that only compares keys to each other.
    /// </summary>
    [Fact]
    public void A_directly_decodable_image_keeps_the_key_it_has_always_had()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        Assert.Equal(
            "a926483c317b30eb9a8568b85a71169d34b31bbbfac17dee350d6f7fe73d16c7",
            cache.GetCacheKey(Sample(), 320));
    }

    private static string Sha256(string value)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task Round_trips_thumbnail_bytes()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));
        var key = cache.GetCacheKey(Sample(), 320);
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        Assert.Null(await cache.TryReadAsync(key, CancellationToken.None));

        await cache.WriteAsync(key, payload, CancellationToken.None);

        Assert.Equal(payload, await cache.TryReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Write_leaves_no_temporary_files_behind()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        var cache = new ThumbnailCache(paths);

        await cache.WriteAsync(cache.GetCacheKey(Sample(), 320), [9, 9, 9], CancellationToken.None);

        var leftovers = Directory.GetFiles(paths.ThumbnailCacheRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }
}
