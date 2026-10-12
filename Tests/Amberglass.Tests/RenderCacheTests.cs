using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Cache;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class RenderCacheTests
{
    private static MediaFile File(string name = "/library/IMG001.RAF", long size = 1000, long ticks = 500) => new()
    {
        FullPath = name,
        FileName = Path.GetFileName(name),
        MediaType = MediaTypeRegistry.GetMediaType(name),
        SizeBytes = size,
        ModifiedUtc = new DateTimeOffset(ticks, TimeSpan.Zero),
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    // ---- What is worth storing ----------------------------------------------------------------

    /// <summary>
    /// Only RAW, and only when it is actually being developed. Re-encoding a camera JPEG would spend
    /// twenty megabytes to save about four hundred milliseconds against decoding the original.
    /// </summary>
    [Theory]
    [InlineData("/library/IMG001.RAF", true, true)]
    [InlineData("/library/IMG001.DNG", true, true)]
    [InlineData("/library/IMG001.CR3", true, true)]
    [InlineData("/library/IMG001.jpg", true, false)]
    [InlineData("/library/IMG001.png", true, false)]
    [InlineData("/library/CLIP.mp4", true, false)]
    // Showing the embedded preview instead of developing: nothing expensive happens, so nothing to keep.
    [InlineData("/library/IMG001.RAF", false, false)]
    public void Stores_only_what_is_expensive_to_produce(string path, bool developing, bool expected)
        => Assert.Equal(expected, RenderCache.IsWorthCaching(File(path), developing));

    // ---- Identity -----------------------------------------------------------------------------

    [Fact]
    public void The_same_file_and_settings_give_the_same_key()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));

        Assert.Equal(
            cache.GetCacheKey(File(), RawDevelopSettings.Default),
            cache.GetCacheKey(File(), RawDevelopSettings.Default));
    }

    /// <summary>
    /// The develop settings are part of a rendition's identity. Without this, changing the exposure
    /// would serve back a picture the application would no longer produce — and it would keep doing
    /// so until the cache was cleared by hand.
    /// </summary>
    [Fact]
    public void Changing_a_develop_setting_changes_the_key()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));
        var baseline = cache.GetCacheKey(File(), RawDevelopSettings.Default);

        var variants = new[]
        {
            RawDevelopSettings.Default with { ExposureStops = 0.5 },
            RawDevelopSettings.Default with { WhiteBalance = RawWhiteBalance.Auto },
            RawDevelopSettings.Default with { Highlights = RawHighlightMode.Rebuild },
            RawDevelopSettings.Default with { NoiseReduction = RawNoiseReduction.Light },
            RawDevelopSettings.Default with { Quality = RawQuality.Fast }
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline, cache.GetCacheKey(File(), variant));
        }
    }

    /// <summary>Switching back finds the earlier renditions still there rather than developing again.</summary>
    [Fact]
    public void Returning_to_previous_settings_returns_to_the_previous_key()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));

        var original = cache.GetCacheKey(File(), RawDevelopSettings.Default);
        cache.GetCacheKey(File(), RawDevelopSettings.Default with { ExposureStops = 1 });

        Assert.Equal(original, cache.GetCacheKey(File(), RawDevelopSettings.Default));
    }

    /// <summary>An externally edited file must miss rather than serve a rendition of its old contents.</summary>
    [Theory]
    [InlineData(2000, 500)]
    [InlineData(1000, 900)]
    public void A_changed_file_misses_the_cache(long size, long ticks)
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));

        Assert.NotEqual(
            cache.GetCacheKey(File(), RawDevelopSettings.Default),
            cache.GetCacheKey(File(size: size, ticks: ticks), RawDevelopSettings.Default));
    }

    // ---- Round trip ---------------------------------------------------------------------------

    [Fact]
    public async Task Stores_and_returns_the_bytes_with_the_renderer()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));
        var key = cache.GetCacheKey(File(), RawDevelopSettings.Default);
        var payload = new byte[] { 1, 2, 3, 4 };

        await cache.WriteAsync(key, DecodedImage.LibRaw, payload, CancellationToken.None);

        var read = await cache.TryReadAsync(key, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(payload, read.Value.Data);
        Assert.Equal(DecodedImage.LibRaw, read.Value.Renderer);
    }

    /// <summary>
    /// The renderer has to survive the round trip: the develop panel warns when a file was rendered
    /// by the platform decoder, which takes no settings. A cache hit that forgot would drop the
    /// warning and leave the controls looking functional.
    /// </summary>
    [Fact]
    public async Task Remembers_that_the_platform_decoder_produced_it()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));
        var key = cache.GetCacheKey(File("/library/PANO.DNG"), RawDevelopSettings.Default);

        await cache.WriteAsync(key, DecodedImage.Platform, [9], CancellationToken.None);

        var read = await cache.TryReadAsync(key, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(DecodedImage.Platform, read.Value.Renderer);
    }

    [Fact]
    public async Task Returns_null_for_a_key_that_was_never_written()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));

        Assert.Null(await cache.TryReadAsync(
            cache.GetCacheKey(File(), RawDevelopSettings.Default), CancellationToken.None));
    }

    /// <summary>
    /// An unknown renderer cannot be encoded in the file name, so it would read back as the wrong
    /// one. Declining to store it is better than storing a rendition that lies about its origin.
    /// </summary>
    [Fact]
    public async Task Declines_to_store_an_unrecognised_renderer()
    {
        using var temp = new TempFolder();
        var cache = new RenderCache(new TestPaths(temp.Path));
        var key = cache.GetCacheKey(File(), RawDevelopSettings.Default);

        await cache.WriteAsync(key, "SomethingElse", [1], CancellationToken.None);

        Assert.Null(await cache.TryReadAsync(key, CancellationToken.None));
    }

    // ---- Budgets ------------------------------------------------------------------------------

    /// <summary>
    /// The pools must not share a budget. A pass through a folder of RAWs writes hundreds of
    /// megabytes; if that counted against the thumbnail limit it would evict the whole thumbnail
    /// library, making browsing slower rather than faster.
    /// </summary>
    [Fact]
    public async Task Clearing_the_render_cache_leaves_thumbnails_alone()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var thumbnail = Path.Combine(paths.ThumbnailCacheRoot, "ab", "thumb.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        await System.IO.File.WriteAllBytesAsync(thumbnail, new byte[512]);

        var cache = new RenderCache(paths);
        await cache.WriteAsync(
            cache.GetCacheKey(File(), RawDevelopSettings.Default),
            DecodedImage.LibRaw,
            new byte[4096],
            CancellationToken.None);

        var maintenance = new RenderCacheMaintenance(
            paths,
            new FixedSettings(AppSettings.Default),
            NullLogger<RenderCacheMaintenance>.Instance);

        var freed = await maintenance.ClearAsync();

        Assert.Equal(4096, freed);
        Assert.True(System.IO.File.Exists(thumbnail), "the thumbnail should have survived");
        Assert.Equal(0, (await maintenance.GetStatisticsAsync()).FileCount);
    }

    /// <summary>Turning the cache off should give the space back, not merely stop adding to it.</summary>
    [Fact]
    public async Task Trimming_a_disabled_cache_empties_it()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var cache = new RenderCache(paths);
        await cache.WriteAsync(
            cache.GetCacheKey(File(), RawDevelopSettings.Default),
            DecodedImage.LibRaw,
            new byte[2048],
            CancellationToken.None);

        var maintenance = new RenderCacheMaintenance(
            paths,
            new FixedSettings(AppSettings.Default with { RenderCacheEnabled = false }),
            NullLogger<RenderCacheMaintenance>.Instance);

        Assert.Equal(2048, await maintenance.TrimAsync());
        Assert.Equal(0, (await maintenance.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task An_unlimited_enabled_cache_is_left_alone()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var cache = new RenderCache(paths);
        await cache.WriteAsync(
            cache.GetCacheKey(File(), RawDevelopSettings.Default),
            DecodedImage.LibRaw,
            new byte[2048],
            CancellationToken.None);

        var maintenance = new RenderCacheMaintenance(
            paths,
            new FixedSettings(AppSettings.Default with
            {
                RenderCacheSizeLimitBytes = AppSettings.UnlimitedCache
            }),
            NullLogger<RenderCacheMaintenance>.Instance);

        Assert.Equal(0, await maintenance.TrimAsync());
        Assert.Equal(1, (await maintenance.GetStatisticsAsync()).FileCount);
    }

    private sealed class FixedSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public event EventHandler<AppSettings>? Changed { add { } remove { } }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
