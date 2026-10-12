namespace Amberglass.Core.Interfaces;

public sealed record CacheStatistics(long TotalBytes, int FileCount)
{
    public static readonly CacheStatistics Empty = new(0, 0);
}

/// <summary>
/// Housekeeping for the disposable thumbnail cache.
/// </summary>
public interface ICacheMaintenance
{
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every cached thumbnail. Returns the bytes freed.</summary>
    Task<long> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts least-recently-used entries until the cache fits the configured limit. Returns the
    /// bytes freed; zero when no limit is set or the cache is already within it.
    /// </summary>
    Task<long> TrimAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the maintainer how much was just written, so trimming can happen on its own once
    /// enough has accumulated rather than being scheduled by every caller.
    /// </summary>
    void NotifyBytesWritten(long bytes);
}

/// <summary>
/// The same housekeeping for the full-resolution render cache, which has its own size budget.
///
/// A distinct interface rather than a second registration of the same one so that the two pools
/// cannot be confused at an injection site — clearing the wrong one would throw away either a
/// library of thumbnails or hours of developing.
/// </summary>
public interface IRenderCacheMaintenance : ICacheMaintenance;
