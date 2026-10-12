using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Cache;

/// <summary>
/// A set of cache directories treated as one budget: enumerate, clear, and evict least-recently-used
/// down to a limit.
///
/// Shared by the two pools rather than written twice. They are separate pools on purpose — thumbnails
/// are kilobytes and renditions are megabytes, so one budget between them would let a single pass
/// through a RAW folder evict every thumbnail in the library — but the housekeeping is identical.
///
/// Eviction can be blunt: entries are content-addressed and independent, so deleting any of them only
/// costs regenerating it if it is wanted again.
/// </summary>
internal sealed class CachePool
{
    /// <summary>
    /// Trim down to slightly under the limit so the next few writes do not immediately trigger
    /// another pass.
    /// </summary>
    private const double TrimTargetRatio = 0.9;

    /// <summary>
    /// Resolved on each use, not captured. The cache location is a setting that takes effect without
    /// a restart, so a pool that remembered where the cache was when it was constructed would keep
    /// tidying the old directory after the user moved it.
    /// </summary>
    private readonly Func<IReadOnlyList<string>> _roots;

    private readonly ILogger _logger;

    public CachePool(Func<IReadOnlyList<string>> roots, ILogger logger)
    {
        _roots = roots;
        _logger = logger;
    }

    public CacheStatistics GetStatistics(CancellationToken cancellationToken)
    {
        var entries = Enumerate(cancellationToken);
        return new CacheStatistics(entries.Sum(e => e.Length), entries.Count);
    }

    public long Clear(CancellationToken cancellationToken)
    {
        var freed = 0L;

        foreach (var entry in Enumerate(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            freed += TryDelete(entry);
        }

        RemoveEmptyShards(cancellationToken);
        return freed;
    }

    public long TrimTo(long limit, CancellationToken cancellationToken)
    {
        var entries = Enumerate(cancellationToken);
        var total = entries.Sum(e => e.Length);
        if (total <= limit)
        {
            return 0L;
        }

        var target = (long)(limit * TrimTargetRatio);
        var freed = 0L;

        // Least recently used first. Access times are unreliable on some volumes (noatime), so
        // last-write is the ordering key — and the render cache touches an entry when it serves it,
        // so for that pool last-write does track use.
        foreach (var entry in entries.OrderBy(e => e.LastWriteTimeUtc))
        {
            if (total - freed <= target)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            freed += TryDelete(entry);
        }

        RemoveEmptyShards(cancellationToken);
        return freed;
    }

    private List<FileInfo> Enumerate(CancellationToken cancellationToken)
    {
        var entries = new List<FileInfo>();

        foreach (var root in _roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        entries.Add(info);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate the cache at {Root}", root);
            }
        }

        return entries;
    }

    private long TryDelete(FileInfo entry)
    {
        try
        {
            var length = entry.Length;
            entry.Delete();
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the cache entry {Path}", entry.FullName);
            return 0;
        }
    }

    private void RemoveEmptyShards(CancellationToken cancellationToken)
    {
        foreach (var root in _roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var shard in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Directory.EnumerateFileSystemEntries(shard).Any())
                    {
                        Directory.Delete(shard);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not tidy empty cache shards under {Root}", root);
            }
        }
    }
}
