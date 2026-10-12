using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Cache;

/// <summary>
/// Housekeeping for the render cache, against its own budget.
///
/// Separate from the thumbnail and proxy pool rather than sharing one limit. Entries here are tens of
/// megabytes where a thumbnail is tens of kilobytes, so a single pass through a folder of RAWs would
/// otherwise evict every thumbnail in the library — turning a feature meant to make browsing faster
/// into one that makes it slower.
/// </summary>
public sealed class RenderCacheMaintenance : IRenderCacheMaintenance
{
    /// <summary>
    /// Bytes written before a trim is considered. Larger than the thumbnail pool's interval in
    /// proportion to the entries: a single rendition can be 20 MB, and trimming after each one would
    /// mean enumerating the whole cache directory on every photograph.
    /// </summary>
    private const long TrimCheckInterval = 256L * 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<RenderCacheMaintenance> _logger;
    private readonly CachePool _pool;

    private long _bytesSinceTrim;
    private int _trimInFlight;

    public RenderCacheMaintenance(IAppPaths paths, ISettingsService settings, ILogger<RenderCacheMaintenance> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _pool = new CachePool(() => [_paths.RenderCacheRoot], logger);
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _pool.GetStatistics(cancellationToken), cancellationToken);

    public Task<long> ClearAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var freed = _pool.Clear(cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            _logger.LogInformation("Cleared the render cache, freeing {Bytes}", ByteSize.Format(freed));
            return freed;
        }, cancellationToken);

    public Task<long> TrimAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var settings = _settings.Current;

            // Switching the cache off is a request to stop spending disk, so the trim empties it
            // rather than merely halting new writes.
            if (!settings.RenderCacheEnabled)
            {
                return _pool.Clear(cancellationToken);
            }

            if (!settings.IsRenderCacheLimited)
            {
                return 0L;
            }

            var freed = _pool.TrimTo(settings.RenderCacheSizeLimitBytes, cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            if (freed > 0)
            {
                _logger.LogInformation(
                    "Trimmed the render cache to its {Limit} limit, freeing {Freed}",
                    ByteSize.Format(settings.RenderCacheSizeLimitBytes), ByteSize.Format(freed));
            }

            return freed;
        }, cancellationToken);

    public void NotifyBytesWritten(long bytes)
    {
        if (!_settings.Current.IsRenderCacheLimited)
        {
            return;
        }

        if (Interlocked.Add(ref _bytesSinceTrim, bytes) < TrimCheckInterval)
        {
            return;
        }

        // Only ever one trim at a time, and never on the caller's thread.
        if (Interlocked.CompareExchange(ref _trimInFlight, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _bytesSinceTrim, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                await TrimAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background render cache trim failed");
            }
            finally
            {
                Interlocked.Exchange(ref _trimInFlight, 0);
            }
        });
    }
}
