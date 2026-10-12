using Amberglass.Core.Interfaces;

namespace Amberglass.Tests;

/// <summary>An <see cref="IAppPaths"/> rooted in a temp folder, shared by the cache tests.</summary>
internal sealed class TestPaths : IAppPaths
{
    public TestPaths(string root)
    {
        AppDataRoot = root;
        DefaultCacheRoot = Path.Combine(root, "Cache");
        CacheRoot = DefaultCacheRoot;
        ThumbnailCacheRoot = Path.Combine(CacheRoot, "Thumbnails");
        VideoProxyCacheRoot = Path.Combine(CacheRoot, "VideoProxy");
        RenderCacheRoot = Path.Combine(CacheRoot, "Renders");
        LogRoot = Path.Combine(root, "Logs");
        CatalogPath = Path.Combine(root, "catalog.db");

        Directory.CreateDirectory(ThumbnailCacheRoot);
        Directory.CreateDirectory(VideoProxyCacheRoot);
        Directory.CreateDirectory(RenderCacheRoot);
        Directory.CreateDirectory(LogRoot);
    }

    public string AppDataRoot { get; }

    public string CacheRoot { get; }

    public string DefaultCacheRoot { get; }

    public string ThumbnailCacheRoot { get; }

    public string VideoProxyCacheRoot { get; }

    public string RenderCacheRoot { get; }

    public string LogRoot { get; }

    public string CatalogPath { get; }

    public string DefaultCatalogPath => CatalogPath;
}
