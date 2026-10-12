using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Cache;

/// <summary>
/// Statistics, clearing, and rolling size-cap eviction for the derived-data cache — both thumbnails
/// and video proxies, since both are disposable and both compete for the same disk budget.
///
/// Eviction is safe to do bluntly: entries are content-addressed and independent, so deleting any
/// of them only costs regenerating it if it is wanted again.
/// </summary>
public sealed class ThumbnailCacheMaintenance : ICacheMaintenance
{
    /// <summary>
    /// Bytes written before a trim is considered. Trimming enumerates the whole cache directory, so
    /// doing it after every thumbnail would cost more than it saves.
    /// </summary>
    private const long TrimCheckInterval = 32L * 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<ThumbnailCacheMaintenance> _logger;

    private readonly CachePool _pool;

    private long _bytesSinceTrim;
    private int _trimInFlight;

    public ThumbnailCacheMaintenance(IAppPaths paths, ISettingsService settings, ILogger<ThumbnailCacheMaintenance> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;

        // Renders are deliberately absent: they are a separate pool with a separate budget.
        _pool = new CachePool(() => [_paths.ThumbnailCacheRoot, _paths.VideoProxyCacheRoot], logger);
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _pool.GetStatistics(cancellationToken), cancellationToken);

    public Task<long> ClearAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var freed = _pool.Clear(cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            _logger.LogInformation("Cleared the cache, freeing {Bytes}", ByteSize.Format(freed));
            return freed;
        }, cancellationToken);

    public Task<long> TrimAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var limit = _settings.Current.CacheSizeLimitBytes;
            if (limit <= AppSettings.UnlimitedCache)
            {
                return 0L;
            }

            var freed = _pool.TrimTo(limit, cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            if (freed > 0)
            {
                _logger.LogInformation(
                    "Trimmed the cache to its {Limit} limit, freeing {Freed}",
                    ByteSize.Format(limit), ByteSize.Format(freed));
            }

            return freed;
        }, cancellationToken);

    public void NotifyBytesWritten(long bytes)
    {
        if (!_settings.Current.IsCacheLimited)
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
                _logger.LogWarning(ex, "Background cache trim failed");
            }
            finally
            {
                Interlocked.Exchange(ref _trimInFlight, 0);
            }
        });
    }

}
