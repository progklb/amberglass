using System.Diagnostics;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview;
using Amberglass.Preview.Cache;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class ThumbnailServicePriorityTests
{

    /// <summary>A generator that blocks until released, so queueing behaviour can be observed.</summary>
    private sealed class BlockingGenerator : IThumbnailGenerator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started;

        public int Cancelled;

        public bool CanHandle(MediaFile file) => true;

        public void Release() => _release.TrySetResult();

        public async Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Started);

            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }

            return [1, 2, 3];
        }
    }

    private static MediaFile FileAt(string path) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public async Task An_interactive_request_does_not_wait_behind_background_work()
    {
        using var temp = new TempFolder();
        var generator = new BlockingGenerator();
        using var service = new ThumbnailService(
            new ThumbnailCache(new TestPaths(temp.Path)),
            [generator],
            NullLogger<ThumbnailService>.Instance);

        // Saturate the background lane with far more work than it has slots for.
        var background = Enumerable.Range(0, 200)
            .Select(i => service.GetThumbnailAsync(FileAt($"/library/bg{i}.jpg"), 320, ThumbnailPriority.Background))
            .ToList();

        // Wait until background work is actually occupying the lane.
        var spin = Stopwatch.StartNew();
        while (Volatile.Read(ref generator.Started) == 0 && spin.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        var startedByBackground = Volatile.Read(ref generator.Started);
        Assert.True(startedByBackground > 0, "background work never started");

        // An interactive request must reach the generator despite the backlog.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interactive = service.GetThumbnailAsync(
            FileAt("/library/selected.jpg"), 1600, ThumbnailPriority.Interactive, timeout.Token);

        while (Volatile.Read(ref generator.Started) <= startedByBackground && !timeout.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        Assert.True(
            Volatile.Read(ref generator.Started) > startedByBackground,
            "the interactive request never started while background work was queued");

        generator.Release();
        Assert.NotNull(await interactive);
        await Task.WhenAll(background);
    }

    [Fact]
    public async Task Cancelling_a_request_stops_its_generator_work()
    {
        using var temp = new TempFolder();
        var generator = new BlockingGenerator();
        using var service = new ThumbnailService(
            new ThumbnailCache(new TestPaths(temp.Path)),
            [generator],
            NullLogger<ThumbnailService>.Instance);

        using var cts = new CancellationTokenSource();
        var request = service.GetThumbnailAsync(FileAt("/library/a.jpg"), 320, ThumbnailPriority.Background, cts.Token);

        var spin = Stopwatch.StartNew();
        while (Volatile.Read(ref generator.Started) == 0 && spin.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, Volatile.Read(ref generator.Cancelled));

        generator.Release();
    }

    [Fact]
    public async Task A_cached_thumbnail_is_returned_without_queueing()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        var cache = new ThumbnailCache(paths);
        var generator = new BlockingGenerator();
        using var service = new ThumbnailService(cache, [generator], NullLogger<ThumbnailService>.Instance);

        var file = FileAt("/library/cached.jpg");
        await cache.WriteAsync(cache.GetCacheKey(file, 320), [9, 9, 9], CancellationToken.None);

        // The generator is blocked, so returning at all proves the cache short-circuits the gate.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = await service.GetThumbnailAsync(file, 320, ThumbnailPriority.Background, timeout.Token);

        Assert.Equal([9, 9, 9], bytes);
        Assert.Equal(0, Volatile.Read(ref generator.Started));
    }
}
