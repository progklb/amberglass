using Amberglass.Core.Interfaces;
using Amberglass.Core.Services;
using Amberglass.Database;
using Amberglass.Metadata.ExifTool;
using Amberglass.Metadata.Xmp;
using Amberglass.Preview;
using Amberglass.Preview.Cache;
using Amberglass.Preview.Audio;
using Amberglass.Preview.Images;
using Amberglass.Preview.Video;
using Amberglass.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Amberglass.UI.Services;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAmberglass(
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

        // DNGs written by Lightroom compress their masks and previews as JPEG XL, which only ImageIO
        // can read here. Elsewhere the DNG readers still handle what is stored uncompressed, Deflate
        // or as JPEG.
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IJpegXlDecoder, ImageIoJpegXlDecoder>();
        }

        services.AddSingleton<ITransparencyMaskReader>(sp => new DngTransparencyMaskReader(
            sp.GetRequiredService<ILogger<DngTransparencyMaskReader>>(),
            sp.GetService<IJpegXlDecoder>()));
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
        //
        // Wrapped so a file is developed once however many callers ask: the preview pane and the
        // viewer both want the same develop, a moment apart.
        services.AddSingleton<IRawDecoder>(sp => new SharedRawDecoder(
            new CompositeRawDecoder(
                OperatingSystem.IsMacOS()
                    ? [
                        ActivatorUtilities.CreateInstance<LibRawImageDecoder>(sp),
                        ActivatorUtilities.CreateInstance<ImageIoRawDecoder>(sp)
                      ]
                    : (IRawDecoder[])[ActivatorUtilities.CreateInstance<LibRawImageDecoder>(sp)],
                sp.GetRequiredService<ILogger<CompositeRawDecoder>>()),
            sp.GetRequiredService<ISettingsService>()));
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
        // ExifTool extracts whatever preview a RAW carries; a DNG may hold a much larger one ExifTool
        // does not hand over, which the wrapper reads first.
        services.AddSingleton<ExifToolPreviewExtractor>();
        services.AddSingleton<IEmbeddedPreviewExtractor>(sp => new DngPreviewExtractor(
            sp.GetRequiredService<ExifToolPreviewExtractor>(),
            sp.GetRequiredService<ILogger<DngPreviewExtractor>>(),
            sp.GetService<IJpegXlDecoder>()));
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
