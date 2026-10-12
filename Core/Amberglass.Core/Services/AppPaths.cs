using Amberglass.Core.Interfaces;

namespace Amberglass.Core.Services;

/// <summary>
/// Application-owned locations.
///
/// Layout:
/// <code>
/// &lt;LocalAppData&gt;/Amberglass/
///     settings.json          preferences — never inside Cache
///     catalog.db             SQLite search catalog — never inside Cache
///     Logs/                  diagnostics — never inside Cache
///     Cache/Thumbnails/      disposable derived data (relocatable)
///     Cache/VideoProxy/      generated low-resolution video
///     Cache/Renders/         developed RAWs at full resolution — own size budget
/// </code>
///
/// Logs and settings sit <b>outside</b> Cache so that clearing or relocating the cache cannot take
/// them with it.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private readonly ISettingsService? _settings;

    public AppPaths()
        : this(null)
    {
    }

    public AppPaths(ISettingsService? settings)
    {
        _settings = settings;
        AppDataRoot = GetAppDataRoot();
        LogRoot = Path.Combine(AppDataRoot, "Logs");

        Directory.CreateDirectory(LogRoot);
        Directory.CreateDirectory(ThumbnailCacheRoot);
        Directory.CreateDirectory(VideoProxyCacheRoot);
        Directory.CreateDirectory(RenderCacheRoot);
    }

    public string AppDataRoot { get; }

    public string LogRoot { get; }

    /// <summary>Honours the user's override, so the value tracks settings without a restart.</summary>
    public string CacheRoot
    {
        get
        {
            var over = _settings?.Current.CacheDirectoryOverride;
            return string.IsNullOrWhiteSpace(over) ? DefaultCacheRoot : over;
        }
    }

    public string DefaultCacheRoot => Path.Combine(AppDataRoot, "Cache");

    /// <summary>Honours the user's override, so a relocated catalog takes effect without a restart.</summary>
    public string CatalogPath
    {
        get
        {
            var over = _settings?.Current.CatalogDirectoryOverride;
            return Path.Combine(string.IsNullOrWhiteSpace(over) ? AppDataRoot : over, "catalog.db");
        }
    }

    public string DefaultCatalogPath => Path.Combine(AppDataRoot, "catalog.db");

    public string ThumbnailCacheRoot => Path.Combine(CacheRoot, "Thumbnails");

    public string VideoProxyCacheRoot => Path.Combine(CacheRoot, "VideoProxy");

    public string RenderCacheRoot => Path.Combine(CacheRoot, "Renders");

    public static string GetAppDataRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        var root = Path.Combine(baseDir, FolderName);
        AdoptFormerFolder(baseDir, root);
        return root;
    }

    internal const string FolderName = "Amberglass";

    /// <summary>What this application was called before it had its name.</summary>
    internal const string FormerFolderName = "BetterDAM";

    /// <summary>
    /// Brings the previous name's folder across, once.
    ///
    /// Everything that cannot be rebuilt lives in here — settings, the keyword library, the catalog
    /// and the working tree of unwritten edits. A rename that quietly left them behind would look
    /// exactly like losing them: the application would open on a first run, with no workspace, no
    /// vocabulary and no pending work, while the real folder sat beside it under the old name.
    ///
    /// Moved rather than copied, so there is one folder afterwards and no question about which of
    /// them is being written to. Only ever when there is nothing at the new name, so this cannot
    /// overwrite a newer install, and failure is not fatal: the application starts on a fresh
    /// folder, which is recoverable by hand, where refusing to start is not.
    /// </summary>
    internal static void AdoptFormerFolder(string baseDir, string root)
    {
        try
        {
            var former = Path.Combine(baseDir, FormerFolderName);

            if (Directory.Exists(root) || !Directory.Exists(former))
            {
                return;
            }

            Directory.Move(former, root);
        }
        catch (Exception)
        {
            // Nothing is logged from here: this runs while the paths that the logger needs are
            // still being worked out.
        }
    }
}
