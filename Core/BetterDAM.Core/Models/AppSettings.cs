namespace BetterDAM.Core.Models;

/// <summary>
/// User preferences. Persisted outside the cache directory — settings must survive "Clear cache".
/// </summary>
public sealed record AppSettings
{
    public const long UnlimitedCache = 0;

    public static readonly AppSettings Default = new();

    /// <summary>
    /// Where derived data is written. Null means the platform default. Useful when the media lives
    /// on an external drive and the boot disk is small.
    /// </summary>
    public string? CacheDirectoryOverride { get; init; }

    /// <summary>
    /// Where the search catalog lives. Null means alongside the other application data. Useful when
    /// the library is large and the boot disk is not.
    /// </summary>
    public string? CatalogDirectoryOverride { get; init; }

    /// <summary>
    /// Size ceiling for the thumbnail cache in bytes. <see cref="UnlimitedCache"/> disables trimming.
    /// When exceeded, the least recently used entries are evicted — the cache is disposable, so
    /// discarding old entries only costs regenerating them if they are needed again.
    /// </summary>
    public long CacheSizeLimitBytes { get; init; } = UnlimitedCache;

    public bool IsCacheLimited => CacheSizeLimitBytes > UnlimitedCache;

    /// <summary>
    /// The workspace open when the application last closed, reopened on launch so browsing picks up
    /// where it left off. Null on a first run, or after the folder is closed.
    /// </summary>
    public string? LastWorkspacePath { get; init; }

    /// <summary>
    /// Recently opened workspaces, most recent first, for the Open Recent menu.
    /// </summary>
    public IReadOnlyList<string> RecentWorkspaces { get; init; } = [];

    /// <summary>
    /// The colours the application paints itself in. Purely cosmetic — nothing about how media is
    /// decoded, displayed or judged depends on it, and the fullscreen viewer stays black regardless.
    /// </summary>
    public AppTheme Theme { get; init; } = AppTheme.Darkroom;

    /// <summary>
    /// Where the selection highlight takes its colour from. Separate from <see cref="Theme"/> on
    /// purpose — how loud a selection should be is not the same question as how dark the
    /// application should be.
    /// </summary>
    public SelectionColour SelectionColour { get; init; } = SelectionColour.System;

    /// <summary>
    /// How the selected folder is marked out. <see cref="SelectionStyle.Standard"/> by default —
    /// the alternative is an experiment, and an experiment nobody opted into is a bug.
    /// </summary>
    public SelectionStyle SelectionStyle { get; init; } = SelectionStyle.Standard;

    /// <summary>
    /// How much the hand-drawn ring wanders off a true ellipse. Below about 0.4 it reads as a plain
    /// oval; above about 1.6 it starts to look scribbled. Clamped rather than validated on the way
    /// in, so a hand-edited settings file cannot produce a ring that misses its own control.
    /// </summary>
    public double HandDrawnRoughness { get; init; } = 1.0;

    public const double MinRoughness = 0.2;
    public const double MaxRoughness = 2.4;

    public double ClampedRoughness => Math.Clamp(HandDrawnRoughness, MinRoughness, MaxRoughness);

    /// <summary>
    /// How brightly the interface is drawn, 1.0 being the theme as designed.
    ///
    /// A darkroom is dim so the eye adapts to the print rather than to the room. This dims the
    /// application the same way — and only the application: a photograph is never touched, because
    /// judging one against a dimmed copy of itself would be judging the wrong picture.
    /// </summary>
    public double InterfaceBrightness { get; init; } = 1.0;

    /// <summary>
    /// Dim, not dark.
    ///
    /// The floor is set by the label chips rather than by ordinary text. Most of this interface is
    /// pale ink on near-black, which only gains contrast as it dims; the chips are ink on a fixed
    /// pale swatch, so they lose it. At a third they were no longer readable, which is a worse
    /// problem than a bright interface.
    /// </summary>
    public const double MinBrightness = 0.45;

    public const double MaxBrightness = 1.0;

    public double ClampedBrightness => Math.Clamp(InterfaceBrightness, MinBrightness, MaxBrightness);

    /// <summary>
    /// Whether the ring draws itself on when a folder is chosen, rather than simply appearing. The
    /// drawing motion is most of the effect; without it the ring is only a wobbly oval.
    /// </summary>
    public bool HandDrawnAnimates { get; init; } = true;

    /// <summary>
    /// Whether the grid is dressed as a contact sheet: a film rebate above each frame, verdicts
    /// written across the picture in grease pencil rather than tucked into a corner badge, film
    /// edges for the splitters, and a first thumbnail that comes up like a print in the tray.
    ///
    /// Off by default like the other experiments, and independent of the hand-drawn selection —
    /// the two share a pencil and a roughness, but wanting one is no reason to be given the other.
    /// Nothing under it touches a photograph: the marks are drawn over the picture, never into it.
    /// </summary>
    public bool ContactSheet { get; init; }

    /// <summary>
    /// The typeface the interface is set in. Separate from the hand-drawn marks, which some will
    /// want without the font and the other way round.
    /// </summary>
    public UiFont UiFont { get; init; } = UiFont.System;

    /// <summary>
    /// Whether the viewer takes over the whole screen or just fills the current one.
    ///
    /// Two different things on macOS: real fullscreen hides the menu bar but moves the window to a
    /// Space of its own, which is heavy-handed for a quick look at a photo. Maximised stays put.
    /// </summary>
    public bool ViewerOpensFullscreen { get; init; }

    /// <summary>
    /// Whether files that are the same photograph are drawn as one tile.
    ///
    /// A camera set to RAW+JPEG writes two files per frame, and a folder of them reads as twice as
    /// many photographs as were taken. Off by default: it hides files, and an application that
    /// hides files without being asked is one you cannot trust to be showing you the folder.
    /// </summary>
    public bool StackPairs { get; init; }

    /// <summary>
    /// Whether the thumbnails are drawn as one sideways-scrolling row with the preview filling the
    /// rest of the window, rather than as a grid.
    ///
    /// Remembered, because it is a way of working rather than a momentary choice: culling a shoot
    /// one frame at a time and laying a whole shoot out are different jobs, and whichever was last
    /// in use is the one to come back to.
    /// </summary>
    public bool Filmstrip { get; init; }

    /// <summary>
    /// Which half of a collapsed pair is drawn. RAW by default — it is the negative, and the file
    /// a photographer means when they say the photograph.
    /// </summary>
    public StackShows StackShows { get; init; } = StackShows.Raw;

    /// <summary>
    /// Whether RAW files are developed for viewing, or shown from the JPEG the camera embedded.
    ///
    /// Developing demosaics the sensor data: more pixels than the preview and no in-camera
    /// processing, at the cost of several seconds per image. The preview is instant and is what the
    /// camera thought the picture should look like.
    /// </summary>
    public bool DevelopRawFiles { get; init; } = true;

    /// <summary>
    /// How RAW files are developed. Persisted so a way of working survives a restart, and applied
    /// to every RAW rather than stored per file — this is a viewer, not an editor, and nothing here
    /// is written back to the photograph.
    /// </summary>
    public RawDevelopSettings RawDevelop { get; init; } = RawDevelopSettings.Default;

    /// <summary>
    /// Whether developed RAW files are kept on disk so that opening the same photograph again is
    /// instant rather than another few seconds of demosaicing.
    ///
    /// A setting because the storage cost is real and unevenly wanted: a rendition of a 26MP frame is
    /// around 6.5 MB where its thumbnail is around 50 KB, so a library of a few thousand RAWs implies
    /// tens of gigabytes. Bounded by <see cref="RenderCacheSizeLimitBytes"/> rather than left to grow,
    /// and disposable — turning it off costs nothing but the time to develop again.
    /// </summary>
    public bool RenderCacheEnabled { get; init; } = true;

    /// <summary>
    /// Size ceiling for the render cache, evicted least-recently-used like the thumbnail cache but
    /// against its own budget. <see cref="UnlimitedCache"/> disables trimming.
    ///
    /// The default holds roughly fifteen hundred renditions of 26MP frames — a generous working set,
    /// and well short of what a whole library would need.
    /// </summary>
    public long RenderCacheSizeLimitBytes { get; init; } = DefaultRenderCacheLimit;

    public const long DefaultRenderCacheLimit = 10L * 1024 * 1024 * 1024;

    public bool IsRenderCacheLimited => RenderCacheSizeLimitBytes > UnlimitedCache;

    /// <summary>
    /// Whether keywords may only be applied from the library.
    ///
    /// The point of a library is consistent filtering, and free text quietly defeats it: the same
    /// ground-texture shot becomes "ground" one day and "sand", "dirt" or "texture" the next, and
    /// none of them find each other again. Restricted, the input filters the library instead of
    /// accepting anything — and offers to add a genuinely new word to the library rather than
    /// blocking it.
    ///
    /// Has no effect until a library exists: with nothing to pick from, there would be nothing to do.
    /// </summary>
    public bool RestrictKeywordsToLibrary { get; init; } = true;

    /// <summary>
    /// The colour labels offered, and what they are called. Editable because the names are the only
    /// part other applications read, and matching them to whatever Bridge or Lightroom is set to is
    /// what lets a label travel between them.
    /// </summary>
    public LabelLibrary Labels { get; init; } = LabelLibrary.Default;

    /// <summary>
    /// Fields hidden from the metadata panel.
    ///
    /// A list of what to <b>hide</b> rather than what to show, which matters for a setting that will
    /// outlive the current set of fields: a field added later is simply absent from anyone's hidden
    /// list and appears for everyone. An allow-list would hide every new field from every existing
    /// user, silently, until they went looking for it.
    /// </summary>
    public IReadOnlyList<MetadataField> HiddenMetadataFields { get; init; } = [];

    public bool IsFieldVisible(MetadataField field) => !HiddenMetadataFields.Contains(field);

    /// <summary>Beyond this many entries, Open Recent stops being a shortcut and becomes a list.</summary>
    public const int MaxRecentWorkspaces = 10;

    /// <summary>
    /// Above this many files, indexing a workspace is offered rather than simply done. Below it the
    /// work is short enough that asking would be more disruptive than the indexing.
    /// </summary>
    public const int IndexPromptThreshold = 5000;

    /// <summary>
    /// Whether each workspace should be indexed, keyed by path — only recorded for workspaces large
    /// enough to have been asked about. Per workspace so the question is answered once, not on
    /// every open.
    /// </summary>
    public IReadOnlyDictionary<string, bool> WorkspaceIndexing { get; init; }
        = new Dictionary<string, bool>();

    public AppSettings WithIndexingChoice(string workspace, bool index)
    {
        var choices = new Dictionary<string, bool>(WorkspaceIndexing, StringComparer.Ordinal)
        {
            [workspace] = index
        };

        return this with { WorkspaceIndexing = choices };
    }

    /// <summary>
    /// Records <paramref name="path"/> as the current workspace and moves it to the front of the
    /// recent list, de-duplicating so reopening the same folder does not fill the menu with it.
    /// </summary>
    public AppSettings WithWorkspace(string path)
    {
        var recent = new List<string> { path };
        recent.AddRange(RecentWorkspaces.Where(p => !string.Equals(p, path, StringComparison.Ordinal)));

        return this with
        {
            LastWorkspacePath = path,
            RecentWorkspaces = recent.Take(MaxRecentWorkspaces).ToList()
        };
    }
}
