using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview;

/// <summary>
/// Walks a workspace and produces everything the caches would otherwise produce on demand: the two
/// thumbnail tiers for every photograph, a full-resolution rendition for every RAW, and — only if
/// asked — a proxy for every clip.
///
/// This is deliberately not a new pipeline. It calls the same services the UI calls, so a file
/// prepared here is byte-for-byte the file that would have been produced by looking at it, and
/// anything already cached is a hit rather than repeated work. That also means the whole thing is
/// safe to cancel or skip: nothing here is required, it is only early.
/// </summary>
public sealed class WorkspacePreparer : IWorkspacePreparer
{
    /// <summary>
    /// How many photographs are developed at once.
    ///
    /// Capped low on purpose. Each develop is an external process holding a full frame, and the
    /// rendition is encoded from another copy — four at a time is already most of a gigabyte, and
    /// going wider trades a machine you can still use for a marginally shorter wait.
    /// </summary>
    private const int MaxParallelism = 4;

    private const int GridThumbnailPixels = 320;
    private const int PreviewThumbnailPixels = 1600;

    private readonly IMediaScanner _scanner;
    private readonly IThumbnailService _thumbnails;
    private readonly IFullImageDecoder _fullImages;
    private readonly IVideoProxyService _proxies;
    private readonly ILogger<WorkspacePreparer> _logger;

    public WorkspacePreparer(
        IMediaScanner scanner,
        IThumbnailService thumbnails,
        IFullImageDecoder fullImages,
        IVideoProxyService proxies,
        ILogger<WorkspacePreparer> logger)
    {
        _scanner = scanner;
        _thumbnails = thumbnails;
        _fullImages = fullImages;
        _proxies = proxies;
        _logger = logger;
    }

    public int Parallelism { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, MaxParallelism);

    public async Task<WorkspaceEstimate> EstimateAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var images = 0;
        var raws = 0;
        var videos = 0;
        var videoBytes = 0L;

        await foreach (var file in _scanner.ScanAsync(workspacePath, new ScanOptions(), cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (file.MediaType)
            {
                case MediaType.Image:
                    images++;
                    if (MediaTypeRegistry.IsRaw(file.FullPath))
                    {
                        raws++;
                    }

                    break;

                case MediaType.Video:
                    videos++;
                    videoBytes += file.SizeBytes;
                    break;
            }
        }

        return new WorkspaceEstimate(images, raws, videos, videoBytes);
    }

    public async Task<PreparationResult> PrepareAsync(
        string workspacePath,
        PreparationOptions options,
        IProgress<PreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<MediaFile>();

        await foreach (var file in _scanner.ScanAsync(workspacePath, new ScanOptions(), cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            files.Add(file);
        }

        var images = files.Where(f => f.MediaType == MediaType.Image).ToList();
        var videos = options.IncludeVideoProxies && _proxies.IsAvailable
            ? files.Where(f => f.MediaType == MediaType.Video).ToList()
            : [];

        var total = images.Count + videos.Count;
        var state = new Counters();

        try
        {
            await PrepareImagesAsync(images, total, state, progress, cancellationToken).ConfigureAwait(false);
            await PrepareVideosAsync(videos, options.ProxyQuality, total, state, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Preparation of {Workspace} was cancelled after {Done} of {Total}",
                workspacePath, state.Completed, total);

            return state.ToResult(cancelled: true);
        }

        _logger.LogInformation(
            "Prepared {Workspace}: {Prepared} file(s), {Failed} failed, {Skipped} skipped",
            workspacePath, state.Prepared, state.Failed, state.Skipped);

        return state.ToResult(cancelled: false);
    }

    /// <summary>
    /// Thumbnails for every photograph, and a full-resolution decode for the RAWs — which is what
    /// fills the render cache, since the decoder writes to it as a side effect of being asked.
    /// </summary>
    private async Task PrepareImagesAsync(
        IReadOnlyList<MediaFile> images,
        int total,
        Counters state,
        IProgress<PreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            images,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                try
                {
                    await _thumbnails.GetThumbnailAsync(file, GridThumbnailPixels, ThumbnailPriority.Background, token)
                        .ConfigureAwait(false);
                    await _thumbnails.GetThumbnailAsync(file, PreviewThumbnailPixels, ThumbnailPriority.Background, token)
                        .ConfigureAwait(false);

                    if (MediaTypeRegistry.IsRaw(file.FullPath))
                    {
                        // The expensive one. Its result is discarded here — the point is the cache
                        // entry the decoder writes on the way past, not the pixels.
                        var decoded = await _fullImages.DecodeAsync(file, token).ConfigureAwait(false);
                        state.AddBytes(decoded?.SizeBytes ?? 0);
                    }

                    state.Succeeded();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One unreadable file must not abandon the other two thousand.
                    _logger.LogWarning(ex, "Could not prepare {File}", file.FullPath);
                    state.Failed_();
                }

                Report(progress, state, total, "Photographs", file.FileName);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Proxies, one at a time. The proxy service already serialises encoding behind its own gate, so
    /// running these in parallel would only queue them differently — and it keeps the machine usable.
    /// </summary>
    private async Task PrepareVideosAsync(
        IReadOnlyList<MediaFile> videos,
        VideoQuality quality,
        int total,
        Counters state,
        IProgress<PreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var file in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _thumbnails.GetThumbnailAsync(file, GridThumbnailPixels, ThumbnailPriority.Background, cancellationToken)
                    .ConfigureAwait(false);

                if (_proxies.HasProxy(file, quality))
                {
                    state.SkippedOne();
                }
                else
                {
                    await _proxies.GetProxyAsync(file, quality, progress: null, cancellationToken).ConfigureAwait(false);
                    state.Succeeded();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not prepare a proxy for {File}", file.FullPath);
                state.Failed_();
            }

            Report(progress, state, total, "Video", file.FileName);
        }
    }

    private static void Report(IProgress<PreparationProgress>? progress, Counters state, int total, string stage, string file)
        => progress?.Report(new PreparationProgress(state.Completed, total, stage, file));

    /// <summary>
    /// Counters shared across the parallel workers. Interlocked rather than a lock: they are only ever
    /// incremented, and read for a progress figure that does not need to be exact.
    /// </summary>
    private sealed class Counters
    {
        private int _prepared;
        private int _failed;
        private int _skipped;
        private long _bytes;

        public int Prepared => _prepared;

        public int Failed => _failed;

        public int Skipped => _skipped;

        public int Completed => _prepared + _failed + _skipped;

        public void Succeeded() => Interlocked.Increment(ref _prepared);

        public void Failed_() => Interlocked.Increment(ref _failed);

        public void SkippedOne() => Interlocked.Increment(ref _skipped);

        public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public PreparationResult ToResult(bool cancelled)
            => new(_prepared, _failed, _skipped, Interlocked.Read(ref _bytes), cancelled);
    }
}
