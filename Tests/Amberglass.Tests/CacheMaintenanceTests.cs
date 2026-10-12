using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.Preview.Cache;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>An in-memory settings service; the on-disk one is covered separately.</summary>
internal sealed class StubSettingsService(AppSettings initial) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial;

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

public class CacheMaintenanceTests
{
    private static ThumbnailCacheMaintenance Create(TestPaths paths, AppSettings settings)
        => new(paths, new StubSettingsService(settings), NullLogger<ThumbnailCacheMaintenance>.Instance);

    /// <summary>Writes a cache entry of a given size, with a write time that sets its LRU order.</summary>
    private static string WriteEntry(TestPaths paths, string name, int bytes, DateTime writtenUtc)
    {
        var shard = Path.Combine(paths.ThumbnailCacheRoot, name[..2]);
        Directory.CreateDirectory(shard);

        var path = Path.Combine(shard, name + ".jpg");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public async Task Statistics_report_size_and_count()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 1000, DateTime.UtcNow);
        WriteEntry(paths, "bb2", 2000, DateTime.UtcNow);

        var stats = await Create(paths, AppSettings.Default).GetStatisticsAsync();

        Assert.Equal(3000, stats.TotalBytes);
        Assert.Equal(2, stats.FileCount);
    }

    [Fact]
    public async Task Statistics_on_an_empty_cache_are_zero()
    {
        using var temp = new TempFolder();

        var stats = await Create(new TestPaths(temp.Path), AppSettings.Default).GetStatisticsAsync();

        Assert.Equal(0, stats.TotalBytes);
        Assert.Equal(0, stats.FileCount);
    }

    [Fact]
    public async Task Clear_removes_every_entry_and_reports_bytes_freed()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 1500, DateTime.UtcNow);
        WriteEntry(paths, "bb2", 2500, DateTime.UtcNow);

        var maintenance = Create(paths, AppSettings.Default);
        var freed = await maintenance.ClearAsync();

        Assert.Equal(4000, freed);
        Assert.Equal(CacheStatistics.Empty, await maintenance.GetStatisticsAsync());
    }

    [Fact]
    public async Task Clear_leaves_settings_and_logs_untouched()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 1000, DateTime.UtcNow);

        var settingsFile = Path.Combine(paths.AppDataRoot, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "{}");
        var logFile = Path.Combine(paths.LogRoot, "amberglass.log");
        await File.WriteAllTextAsync(logFile, "log line");

        await Create(paths, AppSettings.Default).ClearAsync();

        // Settings and logs deliberately live outside the cache directory.
        Assert.True(File.Exists(settingsFile));
        Assert.True(File.Exists(logFile));
    }

    [Fact]
    public async Task Trim_does_nothing_when_no_limit_is_set()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 5000, DateTime.UtcNow);

        var freed = await Create(paths, AppSettings.Default).TrimAsync();

        Assert.Equal(0, freed);
        Assert.Equal(1, (await Create(paths, AppSettings.Default).GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task Trim_does_nothing_when_the_cache_is_within_the_limit()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 1000, DateTime.UtcNow);

        var freed = await Create(paths, new AppSettings { CacheSizeLimitBytes = 10_000 }).TrimAsync();

        Assert.Equal(0, freed);
    }

    [Fact]
    public async Task Trim_evicts_the_oldest_entries_first()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var now = DateTime.UtcNow;
        var oldest = WriteEntry(paths, "aa1", 1000, now.AddHours(-3));
        var middle = WriteEntry(paths, "bb2", 1000, now.AddHours(-2));
        var newest = WriteEntry(paths, "cc3", 1000, now.AddHours(-1));

        // Limit of 2000 trims to 90% of it (1800), so two of the three 1000-byte entries must go.
        var freed = await Create(paths, new AppSettings { CacheSizeLimitBytes = 2000 }).TrimAsync();

        Assert.Equal(2000, freed);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest), "the most recent entry should survive eviction");
    }

    [Fact]
    public async Task Trim_leaves_the_cache_within_the_limit()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            WriteEntry(paths, $"e{i:D2}x", 1000, now.AddMinutes(-i));
        }

        var maintenance = Create(paths, new AppSettings { CacheSizeLimitBytes = 8000 });
        await maintenance.TrimAsync();

        var stats = await maintenance.GetStatisticsAsync();
        Assert.True(stats.TotalBytes <= 8000, $"cache still {stats.TotalBytes} bytes");
    }

    [Fact]
    public async Task Trim_tidies_up_empty_shard_directories()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        WriteEntry(paths, "aa1", 4000, DateTime.UtcNow.AddHours(-1));
        WriteEntry(paths, "bb2", 1000, DateTime.UtcNow);

        await Create(paths, new AppSettings { CacheSizeLimitBytes = 1200 }).TrimAsync();

        Assert.False(Directory.Exists(Path.Combine(paths.ThumbnailCacheRoot, "aa")));
    }
}

public class JsonSettingsServiceTests
{
    private static JsonSettingsService Create(string path)
        => new(path, NullLogger<JsonSettingsService>.Instance);

    [Fact]
    public void Defaults_are_used_when_no_file_exists()
    {
        using var temp = new TempFolder();

        var settings = Create(Path.Combine(temp.Path, "settings.json")).Current;

        Assert.Null(settings.CacheDirectoryOverride);
        Assert.False(settings.IsCacheLimited);
    }

    [Fact]
    public async Task Settings_round_trip_to_disk()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        await Create(path).SaveAsync(new AppSettings
        {
            CacheDirectoryOverride = "/Volumes/Media/cache",
            CacheSizeLimitBytes = 5_000_000
        });

        var reloaded = Create(path).Current;

        Assert.Equal("/Volumes/Media/cache", reloaded.CacheDirectoryOverride);
        Assert.Equal(5_000_000, reloaded.CacheSizeLimitBytes);
    }

    [Fact]
    public async Task Saving_raises_changed()
    {
        using var temp = new TempFolder();
        var service = Create(Path.Combine(temp.Path, "settings.json"));

        AppSettings? observed = null;
        service.Changed += (_, s) => observed = s;

        await service.SaveAsync(new AppSettings { CacheSizeLimitBytes = 42 });

        Assert.Equal(42, observed?.CacheSizeLimitBytes);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        // Unreadable preferences must never stop the application starting.
        Assert.Equal(AppSettings.Default, Create(path).Current);
    }
}

public class AppPathsTests
{
    [Fact]
    public void Cache_root_follows_the_override()
    {
        using var temp = new TempFolder();
        var settings = new StubSettingsService(new AppSettings { CacheDirectoryOverride = temp.Path });

        var paths = new AppPaths(settings);

        Assert.Equal(temp.Path, paths.CacheRoot);
        Assert.Equal(Path.Combine(temp.Path, "Thumbnails"), paths.ThumbnailCacheRoot);
    }

    [Fact]
    public async Task Cache_root_tracks_a_changed_override_without_a_restart()
    {
        using var temp = new TempFolder();
        var settings = new StubSettingsService(AppSettings.Default);
        var paths = new AppPaths(settings);

        Assert.Equal(paths.DefaultCacheRoot, paths.CacheRoot);

        await settings.SaveAsync(new AppSettings { CacheDirectoryOverride = temp.Path });

        Assert.Equal(temp.Path, paths.CacheRoot);
    }

    [Fact]
    public void Logs_live_outside_the_cache_so_clearing_cannot_remove_them()
    {
        var paths = new AppPaths(new StubSettingsService(AppSettings.Default));

        Assert.False(paths.LogRoot.StartsWith(paths.CacheRoot, StringComparison.Ordinal));
        Assert.False(paths.LogRoot.StartsWith(paths.DefaultCacheRoot, StringComparison.Ordinal));
    }
}
