using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Services;
using BetterDAM.Database;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Metadata.Xmp;
using BetterDAM.Preview;
using BetterDAM.Preview.Cache;
using BetterDAM.Preview.Audio;
using BetterDAM.Preview.Images;
using BetterDAM.Preview.Video;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BetterDAM.UI.Services;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBetterDam(
        this IServiceCollection services,
        IAppPaths paths,
        ISettingsService settings)
    {
        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));

        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<ICacheMaintenance, ThumbnailCacheMaintenance>();

        // Its own pool and its own budget: renditions are megabytes, thumbnails kilobytes, and one
        // shared limit would let a pass through a RAW folder evict the whole thumbnail library.
        services.AddSingleton<IRenderCacheMaintenance, RenderCacheMaintenance>();
        services.AddSingleton<RenderCache>();

        services.AddSingleton<IMediaScanner, MediaScanner>();
        services.AddSingleton<IFolderBrowser, FolderBrowser>();
        services.AddSingleton<IFfmpegLocator, FfmpegLocator>();

        services.AddSingleton<ThumbnailCache>();
        // Order matters: the first generator that can handle a file wins. Skia is tried first
        // because it decodes ordinary images without spawning a process.
        services.AddSingleton<IThumbnailGenerator, SkiaImageThumbnailGenerator>();
        services.AddSingleton<IThumbnailGenerator, RawThumbnailGenerator>();
        services.AddSingleton<IThumbnailGenerator, FfmpegVideoThumbnailGenerator>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IWorkspacePreparer, WorkspacePreparer>();
        services.AddSingleton<IKeywordLibraryService, JsonKeywordLibraryService>();
        services.AddSingleton<IKeywordClipboard, KeywordClipboard>();
        services.AddSingleton<ILibRawLocator, LibRawLocator>();

        // LibRaw first because it is the one the develop settings drive; the platform decoder picks
        // up the files it cannot unpack, which is mostly JPEG XL compressed DNG.
        services.AddSingleton<IRawDecoder>(sp => new CompositeRawDecoder(
            OperatingSystem.IsMacOS()
                ? [
                    ActivatorUtilities.CreateInstance<LibRawImageDecoder>(sp),
                    ActivatorUtilities.CreateInstance<ImageIoRawDecoder>(sp)
                  ]
                : (IRawDecoder[])[ActivatorUtilities.CreateInstance<LibRawImageDecoder>(sp)],
            sp.GetRequiredService<ILogger<CompositeRawDecoder>>()));
        services.AddSingleton<IFullImageDecoder, SkiaFullImageDecoder>();

        services.AddSingleton<IVideoInfoProvider, FfprobeVideoInfoProvider>();
        services.AddSingleton<IVideoProxyService, FfmpegVideoProxyService>();
        services.AddSingleton<IVideoFrameSource, FfmpegFrameSource>();

        // CoreAudio on macOS; elsewhere audio is silent until a backend exists for that platform.
        services.AddSingleton<IAudioOutput>(sp => OperatingSystem.IsMacOS()
            ? new CoreAudioOutput(sp.GetRequiredService<ILogger<CoreAudioOutput>>())
            : new SilentAudioOutput());
        services.AddSingleton<IAudioPlayer, FfmpegAudioPlayer>();

        services.AddSingleton<IExifToolLocator, ExifToolLocator>();
        services.AddSingleton<IEmbeddedPreviewExtractor, ExifToolPreviewExtractor>();
        // One ExifTool process, shared by the reader and the writer.
        services.AddSingleton<ExifToolHost>();
        services.AddSingleton<IMetadataProvider, ExifToolMetadataProvider>();
        services.AddSingleton<IMetadataWriter, ExifToolSidecarWriter>();
        // The working tree outlives the session, so it is journalled to disk. Registered ahead of
        // the store because the store replays it as it is constructed.
        services.AddSingleton<PendingChangeJournal>();
        services.AddSingleton<IPendingChangeStore>(sp =>
            new PendingChangeStore(sp.GetRequiredService<PendingChangeJournal>()));
        services.AddSingleton<IBatchMetadataService, BatchMetadataService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<ICatalog, SqliteCatalog>();
        services.AddSingleton<ICatalogIndexer, CatalogIndexer>();

        services.AddTransient<MetadataInspectorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<VideoPlayerViewModel>();
        services.AddTransient<BatchEditViewModel>();
        services.AddTransient<SyncViewModel>();
        services.AddTransient<ChangesViewModel>();
        services.AddTransient<PrepareWorkspaceViewModel>();
        services.AddTransient<KeywordLibraryEditorViewModel>();
        services.AddTransient<MainWindowViewModel>();

        return services;
    }
}
