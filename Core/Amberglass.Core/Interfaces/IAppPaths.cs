namespace Amberglass.Core.Interfaces;

/// <summary>
/// Locations for application-owned data. Everything under <see cref="CacheRoot"/> must be safe to
/// delete: the application rebuilds it from the original media. Logs and settings live outside it.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root for everything the application owns, cache included.</summary>
    string AppDataRoot { get; }

    /// <summary>Current cache location, honouring any user override.</summary>
    string CacheRoot { get; }

    /// <summary>Where the cache lives when the user has not chosen somewhere else.</summary>
    string DefaultCacheRoot { get; }

    string ThumbnailCacheRoot { get; }

    /// <summary>Generated low-resolution video stand-ins. Disposable like everything under the cache.</summary>
    string VideoProxyCacheRoot { get; }

    /// <summary>
    /// Full-resolution renditions of developed RAW files.
    ///
    /// Kept apart from the thumbnail cache rather than mixed in with it, because the two have
    /// nothing in common but disposability: entries here are megabytes rather than kilobytes, and
    /// sharing one size budget would mean a single pass through a RAW folder evicting every
    /// thumbnail in the library.
    /// </summary>
    string RenderCacheRoot { get; }

    string LogRoot { get; }

    /// <summary>
    /// The SQLite catalog. Kept <b>outside</b> the cache: it is derived data, but rebuilding it means
    /// re-reading metadata for the whole library, so it is not something to throw away casually.
    /// </summary>
    string CatalogPath { get; }

    /// <summary>Where the catalog lives when the user has not chosen somewhere else.</summary>
    string DefaultCatalogPath { get; }
}
