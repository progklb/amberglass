using Avalonia;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int PreviewEdgePixels = 1600;

    // Adding items to the UI collection one at a time makes a large scan crawl. Flushing in
    // batches keeps the grid filling visibly without a layout pass per file.
    private const int BatchSize = 64;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(80);

    private readonly IMediaScanner _scanner;
    private readonly IFolderBrowser _folderBrowser;
    private readonly IThumbnailService _thumbnails;
    private readonly IFullImageDecoder _fullImages;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly IPendingChangeStore _pending;
    private readonly IMetadataWriter _writer;
    private readonly ICatalog _catalog;
    private readonly ICatalogIndexer _indexer;
    private readonly ISettingsService _settings;
    private readonly ILogger<MainWindowViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _workspaceIndexCts;
    private CancellationTokenSource? _searchCts;

    public MainWindowViewModel(
        IMediaScanner scanner,
        IFolderBrowser folderBrowser,
        IThumbnailService thumbnails,
        IFullImageDecoder fullImages,
        IFfmpegLocator ffmpeg,
        IPendingChangeStore pending,
        IMetadataWriter writer,
        ICatalog catalog,
        ICatalogIndexer indexer,
        ISettingsService settings,
        MetadataInspectorViewModel inspector,
        VideoPlayerViewModel player,
        BatchEditViewModel batch,
        ILogger<MainWindowViewModel> logger)
    {
        _scanner = scanner;
        _folderBrowser = folderBrowser;
        _thumbnails = thumbnails;
        _fullImages = fullImages;
        _ffmpeg = ffmpeg;
        _pending = pending;
        _writer = writer;
        _catalog = catalog;
        _indexer = indexer;
        _settings = settings;
        _logger = logger;
        Inspector = inspector;
        Player = player;
        Batch = batch;

        // The full pixel size arrives with the metadata, and the loupe needs it to open a RAW at the
        // right magnification before the develop has finished.
        Inspector.RawTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LoupeTargetWidth));

        // The panel writes one file's sidecar on its own; the grid and the catalog are the two
        // places that were showing the pending change and now have to be told what replaced it.
        Inspector.SidecarWritten += OnSidecarWritten;

        // A batch run marks many files at once; refresh whatever is on screen afterwards.
        // A batch run edits the pending store behind the inspector's back, so the panel has to be
        // told to re-read. Without this, pasting keywords onto the selected file left the old ones on
        // screen until something else happened to reload it.
        Batch.Applied += () =>
        {
            PendingChangeCount = _pending.Count;
            _ = Inspector.LoadAsync(SelectedItem);
        };

        _pending.Changed += (_, e) =>
        {
            PendingChangeCount = _pending.Count;

            // Before the save, not after it: an unsaved rating is still a rating, and the tile
            // showing the old one would contradict the inspector sitting next to it.
            RedrawMarksFor(e.FilePath);
        };

        // The store is no longer always empty at startup: it restores the working tree from its
        // journal, and those edits raise no event because they were not made now. Without this the
        // count stayed at nought until the next edit, and a session's recovered work was invisible.
        PendingChangeCount = _pending.Count;

        // Driven off the collection itself rather than the four places that mutate it, so neither
        // the prompt nor the stacks can drift out of step with what is actually on screen. The
        // grid only ever appends to this collection or clears it, which is the whole of what this
        // has to understand.
        MediaItems.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                VisibleItems.Clear();
                _groups.Clear();
                _faces.Clear();
            }
            else if (e.NewItems is not null)
            {
                foreach (MediaItemViewModel added in e.NewItems)
                {
                    Show(added);
                }
            }

            UpdateEmptyState();
            OnPropertyChanged(nameof(StackedAwayCount));
            OnPropertyChanged(nameof(HasStackedAway));
        };

        UpdateEmptyState();

        _stackPairs = _settings.Current.StackPairs;
        _filmstrip = _settings.Current.Filmstrip;
        _stackShows = _settings.Current.StackShows;

        // The window as it was left. Assigned to the fields rather than the properties so that
        // restoring an arrangement does not count as changing it and write the file straight back.
        _layout = _settings.Current.Layout;
        _thumbnailSize = _layout.ClampedThumbnailSize;
        _isFolderPanelVisible = !_layout.FolderPanelCollapsed;
        _isInspectorVisible = !_layout.InspectorCollapsed;
        _previewOnTop = _layout.PreviewOnTop;

        RecentWorkspaces = new ObservableCollection<string>(_settings.Current.RecentWorkspaces);
        _viewerOpensFullscreen = _settings.Current.ViewerOpensFullscreen;
        _developRawFiles = _settings.Current.DevelopRawFiles;

        var develop = _settings.Current.RawDevelop;
        _highlights = develop.Highlights;
        _whiteBalance = develop.WhiteBalance;
        _exposureStops = develop.ExposureStops;
        _noiseReduction = develop.NoiseReduction;
        _rawQuality = develop.Quality;

        _contactSheet = _settings.Current.ContactSheet;

        RebuildLabelChips();
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildLabelChips();

            // Recolouring a label in Settings has to reach the grid, which is drawing that colour.
            RedrawMarks();

            // The dividers are a line normally and a film rebate under the contact sheet, and the
            // view sets their width itself rather than binding it.
            ContactSheet = _settings.Current.ContactSheet;
        });

        // Otherwise the kind toggles start unticked on an empty query, which reads as "showing
        // nothing" when it means "showing everything".
        ReadFiltersFromQuery();

        StatusText = _ffmpeg.IsAvailable
            ? "Ready. Choose a folder to begin."
            : "Ready. FFmpeg was not found — video thumbnails are unavailable.";
    }

    public MetadataInspectorViewModel Inspector { get; }

    public VideoPlayerViewModel Player { get; }

    public BatchEditViewModel Batch { get; }

    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    /// <summary>
    /// Every file the grid is showing, stacked or not.
    ///
    /// Stays the whole list on purpose. Indexing, marks, pending changes and the Changes review
    /// all walk this, and every one of them would be wrong to skip the half of a pair that happens
    /// to be hidden — a rating written to the RAW is no less real for the JPEG being on top.
    /// </summary>
    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = [];

    /// <summary>
    /// What the grid draws: one tile per photograph when pairs are collapsed, otherwise the lot.
    ///
    /// A second collection rather than a filter over the first, because the two answer different
    /// questions and the difference matters. This one is the picture on screen — the selection
    /// moves through it and the frame numbers count it.
    /// </summary>
    public ObservableCollection<MediaItemViewModel> VisibleItems { get; } = [];

    /// <summary>Every tile of a photograph, by what makes them the same one.</summary>
    private readonly Dictionary<string, List<MediaItemViewModel>> _groups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each group's tile sits in <see cref="VisibleItems"/>.</summary>
    private readonly Dictionary<string, int> _faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Places one newly-arrived file in the grid.
    ///
    /// Written to take one file at a time because that is how a scan arrives — a batch at a time,
    /// with a pair's two halves often landing in different batches. Arranging the whole list again
    /// on every flush would be both quadratic and a grid that resets itself twice a second while
    /// it fills. <see cref="MediaStacking.Arrange"/> is the same rule stated as one pass over a
    /// finished list, and a test walks a shuffled folder through both to prove they agree.
    /// </summary>
    private void Show(MediaItemViewModel item)
    {
        if (!StackPairs || MediaStacking.KeyOf(item.File) is not { } key)
        {
            item.FrameNumber = VisibleItems.Count + 1;
            VisibleItems.Add(item);
            return;
        }

        if (!_groups.TryGetValue(key, out var group))
        {
            group = [];
            _groups[key] = group;
        }

        group.Add(item);

        if (!_faces.TryGetValue(key, out var slot))
        {
            _faces[key] = VisibleItems.Count;
            item.FrameNumber = VisibleItems.Count + 1;
            VisibleItems.Add(item);
        }
        else if (MediaStacking.IsBetterFace(item.File, VisibleItems[slot].File, StackShows))
        {
            // The better half of the pair arrived second. It takes the tile where it stands, so
            // nothing moves — only the picture in it changes.
            var replaced = VisibleItems[slot];
            item.FrameNumber = replaced.FrameNumber;
            replaced.Stacked = [];
            VisibleItems[slot] = item;

            if (ReferenceEquals(SelectedItem, replaced))
            {
                SelectedItem = item;
            }
        }

        var face = VisibleItems[_faces[key]];
        face.Stacked = group
            .Where(m => !ReferenceEquals(m, face))
            .Select(m => m.File)
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lays the grid out again, for when the answer to "which files are one tile" has changed.
    ///
    /// Everything goes back through <see cref="Show"/> rather than through a second arrangement of
    /// its own, so there is one rule about what the grid looks like and no way for a rebuild to
    /// disagree with a scan.
    /// </summary>
    private void RebuildVisible()
    {
        var chosen = SelectedItem;

        VisibleItems.Clear();
        _groups.Clear();
        _faces.Clear();

        foreach (var item in MediaItems)
        {
            item.Stacked = [];
        }

        foreach (var item in MediaItems)
        {
            Show(item);
        }

        // The file that was selected may now be underneath another. Following it to whatever is
        // drawn in its place keeps the inspector on the photograph the user was looking at.
        if (chosen is not null && !VisibleItems.Contains(chosen))
        {
            SelectedItem = MediaStacking.KeyOf(chosen.File) is { } key && _faces.TryGetValue(key, out var slot)
                ? VisibleItems[slot]
                : null;
        }
    }

    /// <summary>
    /// Whether files that are the same photograph are drawn as one tile. A camera set to RAW+JPEG
    /// writes two files per frame, and a folder of them otherwise reads as twice as many
    /// photographs as were taken.
    /// </summary>
    [ObservableProperty]
    private bool _stackPairs;

    /// <summary>
    /// One sideways-scrolling row of thumbnails with the preview filling the rest, rather than a
    /// grid. The tiles, their badges and their marks are unchanged — only how they are laid out.
    /// </summary>
    [ObservableProperty]
    private bool _filmstrip;

    partial void OnFilmstripChanged(bool value)
    {
        if (_settings.Current.Filmstrip != value)
        {
            _ = _settings.SaveAsync(_settings.Current with { Filmstrip = value });
        }

        // The row that was being looked at should still be the row being looked at.
        if (SelectedItem is not null)
        {
            ScrollSelectionIntoView?.Invoke(SelectedItem);
        }
    }

    /// <summary>
    /// Raised when the layout has changed under the selection and the view needs to bring it back
    /// into sight. The view model has no business knowing about scrolling, but it is the only thing
    /// that knows the layout changed.
    /// </summary>
    public event Action<MediaItemViewModel>? ScrollSelectionIntoView;

    [RelayCommand]
    private void ToggleFilmstrip() => Filmstrip = !Filmstrip;

    partial void OnStackPairsChanged(bool value)
    {
        RebuildVisible();
        OnPropertyChanged(nameof(StackedAwayCount));
        OnPropertyChanged(nameof(HasStackedAway));

        if (_settings.Current.StackPairs != value)
        {
            _ = _settings.SaveAsync(_settings.Current with { StackPairs = value });
        }
    }

    /// <summary>Which half of a collapsed pair is drawn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRaw))]
    private StackShows _stackShows;

    partial void OnStackShowsChanged(StackShows value)
    {
        RebuildVisible();

        if (_settings.Current.StackShows != value)
        {
            _ = _settings.SaveAsync(_settings.Current with { StackShows = value });
        }
    }

    /// <summary>
    /// How many files are behind another. Shown in the status bar, because the count beside it is
    /// of files and the grid is of photographs: a search reporting 205 matches over a hundred-odd
    /// tiles is a discrepancy the user would otherwise have to work out for themselves.
    /// </summary>
    public int StackedAwayCount => MediaItems.Count - VisibleItems.Count;

    public bool HasStackedAway => StackedAwayCount > 0;

    /// <summary>The radio buttons bind to this pair rather than to the enum.</summary>
    public bool ShowsRaw
    {
        get => StackShows == StackShows.Raw;
        set => StackShows = value ? StackShows.Raw : StackShows.Jpeg;
    }

    /// <summary>
    /// Storage provider from the active window, supplied by the view. The ViewModel deliberately
    /// does not reach for the window itself.
    /// </summary>
    public IStorageProvider? StorageProvider { get; set; }

    [ObservableProperty]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoupeSource))]
    [NotifyPropertyChangedFor(nameof(LoupeTargetWidth))]
    private Bitmap? _preview;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>
    /// What the loupe magnifies: the full-resolution decode when there is one, otherwise the cached
    /// preview.
    ///
    /// Falling back rather than waiting is the point. The loupe always shows real pixels at 1:1 —
    /// before the decode lands that is the 1600px preview, which is a genuine if modest magnification
    /// and perfectly sharp, and when the decode arrives the same spot simply becomes more detailed.
    /// The alternative, an empty box for the several seconds a RAW takes, would make the feature
    /// useless exactly when it is wanted.
    /// </summary>
    public Bitmap? LoupeSource => FullPreview ?? Preview;

    /// <summary>Whether the loupe is showing the photograph itself rather than a rendition of it.</summary>
    public bool IsLoupeFullResolution => FullPreview is not null;

    /// <summary>
    /// The pixel width that 100% in the loupe refers to.
    ///
    /// Known exactly once the file has been decoded. Before that it comes from the metadata already
    /// read for the inspector, which matters because it is the only way the loupe can open at the
    /// right magnification on a RAW whose develop has not finished — the embedded preview is a
    /// quarter of the size, and scaling to that would make the picture jump when the develop landed.
    /// Zero when nothing knows, which leaves the loupe at the source's own 1:1.
    /// </summary>
    public double LoupeTargetWidth
        => FullPreview?.PixelSize.Width ?? MetadataPixelWidth() ?? Preview?.PixelSize.Width ?? 0;

    /// <summary>
    /// The full pixel width according to ExifTool's Composite:ImageSize.
    ///
    /// Composite deliberately: on a RAF, File:ImageWidth is 4416 — the embedded preview — while the
    /// sensor image is 6240 wide. Reading the obvious tag would give exactly the wrong number.
    /// </summary>
    private double? MetadataPixelWidth()
    {
        var size = Inspector.RawTags
            .FirstOrDefault(tag => tag.QualifiedName is "Composite:ImageSize")?.Value;

        if (size is null)
        {
            return null;
        }

        var separator = size.IndexOf('x');
        return separator > 0
               && double.TryParse(size[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
               && width > 0
            ? width
            : null;
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private double _thumbnailSize = LayoutSettings.Default.ThumbnailSize;

    partial void OnThumbnailSizeChanged(double value) => RememberLayout(l => l with { ThumbnailSize = value });

    /// <summary>
    /// The arrangement of the window as it stands, kept in step with what is on screen and written
    /// out shortly afterwards.
    ///
    /// Held here rather than read back from settings each time because a drag changes it many times
    /// a second: the record is updated as the panel moves, and the file is written once the hand
    /// has stopped.
    /// </summary>
    private LayoutSettings _layout = LayoutSettings.Default;

    private CancellationTokenSource? _layoutSaveCts;

    /// <summary>
    /// Long enough that dragging a splitter or a slider is one write rather than one per frame,
    /// short enough that closing the window straight afterwards still catches it.
    /// </summary>
    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(400);

    private void RememberLayout(Func<LayoutSettings, LayoutSettings> change)
    {
        var updated = change(_layout);
        if (updated == _layout)
        {
            return;
        }

        _layout = updated;
        ScheduleLayoutSave();
    }

    private void ScheduleLayoutSave()
    {
        _layoutSaveCts?.Cancel();
        _layoutSaveCts?.Dispose();
        _layoutSaveCts = new CancellationTokenSource();

        var token = _layoutSaveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LayoutSaveDelay, token).ConfigureAwait(false);

                // Read Current at the last moment: something else may have saved a different part
                // of the settings while the hand was still moving.
                await _settings.SaveAsync(_settings.Current with { Layout = _layout }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // A window arrangement is not worth interrupting anyone over.
                _logger.LogDebug(ex, "Could not save the layout");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Called by the view once a splitter has been let go, with what the drag actually left behind.
    /// The view owns the widths — a binding would be fighting the splitter for them — so this is
    /// the only way the state that gets saved can know what happened.
    /// </summary>
    public void RememberFolderPanelWidth(double width)
        => RememberLayout(l => width > 0 ? l with { FolderPanelWidth = width } : l);

    public void RememberInspectorWidth(double width)
        => RememberLayout(l => width > 0 ? l with { InspectorWidth = width } : l);

    public void RememberBottomPaneHeight(double height)
        => RememberLayout(l => height > 0 ? l with { BottomPaneHeight = height } : l);

    /// <summary>What the view should open the panels at, and put them back to after a collapse.</summary>
    public double FolderPanelWidth => _layout.ClampedFolderPanelWidth;

    public double InspectorWidth => _layout.ClampedInspectorWidth;

    public double BottomPaneHeight => _layout.ClampedBottomPaneHeight;

    [ObservableProperty]
    private string? _currentFolderPath;

    /// <summary>The open workspace, or null when none is. Everything else keys off this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private string? _workspacePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private string? _workspaceName;

    public bool HasWorkspace => WorkspacePath is not null;

    public string WindowTitle => WorkspaceName is null ? "BetterDAM" : $"{WorkspaceName} — BetterDAM";

    /// <summary>
    /// Names what will actually be searched, since the scope is the workspace rather than
    /// everything. Follows the Everywhere toggle so the field never claims the wrong scope.
    /// </summary>
    public string SearchWatermark => !HasWorkspace || SearchEverywhere
        ? "Search everything indexed"
        : $"Search {WorkspaceName}";

    /// <summary>
    /// The search vocabulary, for the help in the filter popup and the list offered at a colon.
    /// Read straight from the parser's own catalogue rather than restated here, so the help cannot
    /// describe a field that does not work or miss one that does.
    /// </summary>
    public static IReadOnlyList<SearchField> SearchHelp => SearchFields.All;

    /// <summary>
    /// What to offer for what is currently half-typed. Empty when the box is not asking, which is
    /// also what hides the popup.
    /// </summary>
    public ObservableCollection<SearchSuggestionItem> FieldSuggestions { get; } = [];

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    public bool HasFieldSuggestions => FieldSuggestions.Count > 0;

    /// <summary>
    /// Keywords in scope, with counts, cached because this is consulted on every keystroke.
    ///
    /// The catalog rather than the library: offering a keyword nothing carries is a dead end, and
    /// the catalog is also where words that arrived from another application show up — which are
    /// exactly the ones worth finding.
    /// </summary>
    private IReadOnlyList<KeywordUsage> _keywordsInScope = [];

    /// <summary>Labels in use, for the same reason and from the same place as the keywords.</summary>
    private IReadOnlyList<LabelUsage> _labelsInScope = [];

    private string? _keywordsLoadedFor;

    /// <summary>
    /// The last thing asked for, so the offer can be recomputed once the catalog answers.
    ///
    /// Without this the first keystroke after opening a workspace shows an empty list and nothing
    /// ever corrects it — the list is built before the query returns, and only another keystroke
    /// would rebuild it.
    /// </summary>
    private (string? Text, int Caret)? _lastSuggestionAt;

    /// <summary>
    /// Reloads the keyword list when the scope changes. Cheap to call: it does nothing unless the
    /// workspace it was loaded for has changed.
    /// </summary>
    private async Task EnsureKeywordsLoadedAsync()
    {
        var scope = SearchEverywhere ? null : WorkspacePath;

        if (string.Equals(_keywordsLoadedFor, scope ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _keywordsInScope = await _catalog.GetKeywordsAsync(scope).ConfigureAwait(true);
            _labelsInScope = await _catalog.GetLabelsAsync(scope).ConfigureAwait(true);
            _keywordsLoadedFor = scope ?? string.Empty;

            // Recompute what is on screen now there is something to offer. Safe from recursion: the
            // cache key now matches, so the call below returns without loading again.
            if (_lastSuggestionAt is { } at && HasFieldSuggestions)
            {
                UpdateFieldSuggestions(at.Text, at.Caret);
            }

            RebuildKeywordChips();
        }
        catch (Exception ex)
        {
            // A missing suggestion list is a small loss; failing the keystroke is not.
            _logger.LogDebug(ex, "Could not read keywords for suggestions");
            _keywordsInScope = [];
            _labelsInScope = [];
        }
    }

    /// <summary>Recomputes what to offer for a caret position. True when the popup should be open.</summary>
    public bool UpdateFieldSuggestions(string? text, int caret)
    {
        _lastSuggestionAt = (text, caret);

        var request = SearchSuggestion.At(text, caret);

        FieldSuggestions.Clear();

        switch (request.Kind)
        {
            case SuggestionKind.Field:
                foreach (var field in SearchFields.Matching(request.Prefix))
                {
                    FieldSuggestions.Add(SearchSuggestionItem.ForField(field));
                }

                break;

            case SuggestionKind.Value:
                foreach (var item in ValuesFor(request.Field, request.Prefix))
                {
                    FieldSuggestions.Add(item);
                }

                break;
        }

        SelectedSuggestionIndex = FieldSuggestions.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasFieldSuggestions));

        // Loaded after answering, so the first keystroke is never blocked on a query; the next one
        // has the list.
        if (request.Kind == SuggestionKind.Value && request.Field is "keyword" or "label")
        {
            _ = EnsureKeywordsLoadedAsync();
        }

        return FieldSuggestions.Count > 0;
    }

    private const int MaxSuggestions = 40;

    private IEnumerable<SearchSuggestionItem> ValuesFor(string field, string prefix)
    {
        bool Matches(string value) =>
            prefix.Length == 0 || value.Contains(prefix, StringComparison.OrdinalIgnoreCase);

        switch (field)
        {
            case "keyword":
                // Already ordered by how many files carry them, so the most useful come first.
                return _keywordsInScope
                    .Where(k => Matches(k.Value))
                    .Take(MaxSuggestions)
                    .Select(k => SearchSuggestionItem.ForValue(k.Value, k.Count));

            case "label":
                // What is actually on the files, then anything the library defines that nothing
                // carries yet — the first are what a filter will find, the second are what the
                // vocabulary says ought to exist.
                var inUse = _labelsInScope
                    .Where(l => Matches(l.Value))
                    .Select(l => SearchSuggestionItem.ForValue(l.Value, l.Count));

                var unused = _settings.Current.Labels.Labels
                    .Select(l => l.Name)
                    .Where(name => Matches(name) &&
                        !_labelsInScope.Any(l => string.Equals(l.Value, name, StringComparison.OrdinalIgnoreCase)))
                    .Select(name => SearchSuggestionItem.ForValue(name, 0));

                return inUse
                    .Concat(unused)
                    .Append(SearchSuggestionItem.ForValue("none", null));

            case "type":
                return new[] { "raw", "jpg", "video", "image" }
                    .Where(Matches)
                    .Select(v => SearchSuggestionItem.ForValue(v, null));

            case "orientation":
                return new[] { "portrait", "landscape", "square" }
                    .Where(Matches)
                    .Select(v => SearchSuggestionItem.ForValue(v, null));

            case "flag":
                return new[] { "accepted", "rejected", "none" }
                    .Where(Matches)
                    .Select(v => SearchSuggestionItem.ForValue(v, null));

            // Ratings and dates are written, not chosen from a list, and a filename is whatever it is.
            default:
                return [];
        }
    }

    public void DismissFieldSuggestions()
    {
        if (FieldSuggestions.Count == 0)
        {
            return;
        }

        FieldSuggestions.Clear();
        SelectedSuggestionIndex = -1;
        OnPropertyChanged(nameof(HasFieldSuggestions));
    }

    // ---- Filter controls -------------------------------------------------------------------------

    /// <summary>
    /// Set while a control is rewriting the query, so the write-back does not immediately re-read
    /// and fight what was just set.
    /// </summary>
    private bool _syncingFilters;

    /// <summary>
    /// How many stars the query asks for, 0 when it asks for none. Written by the star cycle and by
    /// reading the query back; never bound two-way.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingFilterSummary))]
    private int _filterRating;

    /// <summary>True when the query asks for exactly that many stars rather than that many and up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingFilterSummary))]
    private bool _filterRatingExact;

    [ObservableProperty]
    private bool _filterRaw;

    [ObservableProperty]
    private bool _filterJpeg;

    [ObservableProperty]
    private bool _filterVideo;

    [ObservableProperty]
    private bool _filterAccepted;

    [ObservableProperty]
    private bool _filterRejected;

    [ObservableProperty]
    private bool _filterUnflagged;

    partial void OnFilterRawChanged(bool value) => WriteKinds();

    partial void OnFilterJpegChanged(bool value) => WriteKinds();

    partial void OnFilterVideoChanged(bool value) => WriteKinds();

    partial void OnFilterAcceptedChanged(bool value) => WriteFlags();

    partial void OnFilterRejectedChanged(bool value) => WriteFlags();

    partial void OnFilterUnflaggedChanged(bool value) => WriteFlags();

    /// <summary>
    /// One chip per label in the library, plus "No label". Rebuilt when the library changes so a
    /// rename in Settings shows here without a restart.
    /// </summary>
    public ObservableCollection<LabelFilterChip> LabelChips { get; } = [];

    private void RebuildLabelChips()
    {
        LabelChips.Clear();

        foreach (var label in _settings.Current.Labels.Labels)
        {
            LabelChips.Add(new LabelFilterChip(label.Name, label.Colour, ToggleLabelFilter));
        }

        // Files with no label at all. Written as the bare word, which the parser understands and
        // which reads sensibly in the box.
        LabelChips.Add(new LabelFilterChip("No label", null, ToggleLabelFilter));
    }

    private void ToggleLabelFilter(LabelFilterChip chip) => WriteFilter(() =>
    {
        var chosen = LabelChips.Where(c => c.IsSelected).Select(c => c.Term).ToList();

        SearchText = SearchQueryText.WithField(
            SearchText, "label", chosen.Count == 0 ? null : string.Join(',', chosen));
    });

    /// <summary>
    /// Whether the syntax reference is showing. Closed by default: it is worth having and worth
    /// finding, but it is a reference, and a reference does not need to be read every time the panel
    /// is opened.
    /// </summary>
    [ObservableProperty]
    private bool _searchHelpExpanded;

    [RelayCommand]
    private void ToggleSearchHelp() => SearchHelpExpanded = !SearchHelpExpanded;

    /// <summary>
    /// Keywords in the workspace, ticked to filter by them.
    ///
    /// Built from the catalog rather than the keyword library, which is the same choice the search
    /// suggestions make and for the same reason: a vocabulary word nothing carries filters to an
    /// empty view, and a word that arrived from another application is invisible in the library but
    /// is exactly what someone wants to find.
    /// </summary>
    public ObservableCollection<KeywordFilterChip> KeywordChips { get; } = [];

    /// <summary>
    /// Which keywords are ticked, held apart from the chips because the list is rebuilt whenever the
    /// search narrows. Keeping it in the chips would lose every tick the moment the user typed.
    /// </summary>
    private readonly HashSet<string> _selectedKeywords = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private string? _keywordFilterSearch;

    /// <summary>
    /// False asks for any of the ticked keywords, true for all of them.
    ///
    /// Explicit rather than inferred: with two keywords ticked, "any" and "all" are both reasonable
    /// readings and they return very different sets. The query says which — commas for any, repeated
    /// terms for all — so the switch is only making a distinction the syntax already draws.
    /// </summary>
    [ObservableProperty]
    private bool _keywordFilterMatchAll;

    public bool HasKeywordChips => KeywordChips.Count > 0;

    /// <summary>Says why the list is empty, which "no keywords" alone would not.</summary>
    public string KeywordFilterEmptyText
        => _keywordsInScope.Count == 0
            ? "No keywords in this workspace yet."
            : "No keywords match.";

    /// <summary>
    /// Whether the keyword list is showing. Closed by default: it is the one filter that needs a
    /// scrolling list rather than a row of toggles, and left open it makes the panel twice the
    /// height of everything else in it for a filter most searches do not use.
    /// </summary>
    [ObservableProperty]
    private bool _keywordListExpanded;

    [RelayCommand]
    private void ToggleKeywordList() => KeywordListExpanded = !KeywordListExpanded;

    /// <summary>
    /// What is ticked, for when the list is closed over it.
    ///
    /// A section closed over a running filter would make the panel misrepresent the query, so the
    /// ticked words stay on show as pills — the same shape the inspector gives a keyword, because
    /// they are the same thing.
    /// </summary>
    public ObservableCollection<KeywordFilterPill> SelectedKeywordPills { get; } = [];

    /// <summary>
    /// A safety net rather than the thing keeping the section short — the pills wrap, so the usual
    /// case looks after itself. This is for someone who has ticked half the workspace.
    /// </summary>
    private const int MaxKeywordPills = 12;

    public string KeywordPillOverflow { get; private set; } = string.Empty;

    public bool HasKeywordPillOverflow => KeywordPillOverflow.Length > 0;

    /// <summary>
    /// "any of" or "all of", which is inside the fold with the switch that sets it.
    ///
    /// Worth saying only when there is more than one word, since with one the two mean the same and
    /// the label would be noise.
    /// </summary>
    public string KeywordFilterMode => _selectedKeywords.Count > 1
        ? KeywordFilterMatchAll ? "all of" : "any of"
        : string.Empty;

    public bool ShowKeywordFilterMode => KeywordFilterMode.Length > 0;

    /// <summary>
    /// "Keywords", or "Keywords (5)" while the list is open.
    ///
    /// Open, the pills are put away and the list scrolls, so there is no longer anything on screen
    /// saying how many are ticked — the ticks below the fold are as good as invisible. Shut, the
    /// pills say it better than a number would.
    /// </summary>
    public string KeywordHeaderText => KeywordListExpanded && _selectedKeywords.Count > 0
        ? $"Keywords ({_selectedKeywords.Count})"
        : "Keywords";

    private void RebuildKeywordPills()
    {
        SelectedKeywordPills.Clear();

        var names = _selectedKeywords.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();

        foreach (var name in names.Take(MaxKeywordPills))
        {
            SelectedKeywordPills.Add(new KeywordFilterPill(name, RemoveKeywordFilter));
        }

        KeywordPillOverflow = names.Count > MaxKeywordPills
            ? $"+{names.Count - MaxKeywordPills} more"
            : string.Empty;

        OnPropertyChanged(nameof(KeywordPillOverflow));
        OnPropertyChanged(nameof(HasKeywordPillOverflow));
        OnPropertyChanged(nameof(KeywordFilterMode));
        OnPropertyChanged(nameof(ShowKeywordFilterMode));
        OnPropertyChanged(nameof(HasSelectedKeywords));
        OnPropertyChanged(nameof(ShowKeywordSummary));
        OnPropertyChanged(nameof(KeywordHeaderText));
    }

    /// <summary>
    /// Drops one keyword from the filter, from its pill.
    ///
    /// Rebuilding rather than only rewriting the query: the tick in the list has to come off too, and
    /// the list is not on screen to have been clicked.
    /// </summary>
    private void RemoveKeywordFilter(string name)
    {
        _selectedKeywords.Remove(name);

        RebuildKeywordChips();
        WriteKeywords();
    }

    public bool HasSelectedKeywords => _selectedKeywords.Count > 0;

    /// <summary>Only worth the room when the list is closed and there is something to report.</summary>
    public bool ShowKeywordSummary => !KeywordListExpanded && HasSelectedKeywords;

    partial void OnKeywordListExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowKeywordSummary));
        OnPropertyChanged(nameof(KeywordHeaderText));
    }

    partial void OnKeywordFilterSearchChanged(string? value)
    {
        // Searching a list that is not on screen would look like nothing happening.
        if (!string.IsNullOrEmpty(value))
        {
            KeywordListExpanded = true;
        }

        RebuildKeywordChips();
    }

    partial void OnKeywordFilterMatchAllChanged(bool value)
    {
        OnPropertyChanged(nameof(KeywordFilterMode));
        OnPropertyChanged(nameof(ShowKeywordFilterMode));

        // Only worth rewriting when it changes the result: one keyword means the same either way.
        if (!_syncingFilters && _selectedKeywords.Count > 1)
        {
            WriteKeywords();
        }
    }

    /// <summary>
    /// Loads the keyword list if it has not been read yet. Called when the filter popup opens, so
    /// the list is there the first time it is looked at rather than one open behind.
    /// </summary>
    public void PrepareFilters() => _ = EnsureKeywordsLoadedAsync();

    private const int MaxKeywordChips = 200;

    private void RebuildKeywordChips()
    {
        var search = KeywordFilterSearch ?? string.Empty;

        bool Matches(string value) =>
            search.Length == 0 || value.Contains(search, StringComparison.OrdinalIgnoreCase);

        KeywordChips.Clear();

        // Ticked ones first and regardless of the search, so narrowing the list never hides what is
        // currently being filtered by — the one thing that would make the panel lie about the query.
        foreach (var name in _selectedKeywords.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = _keywordsInScope
                .FirstOrDefault(k => string.Equals(k.Value, name, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;

            KeywordChips.Add(new KeywordFilterChip(name, count, true, ToggleKeywordFilter));
        }

        // Alphabetical for reading, capped by usage — see KeywordFilterList.Arrange for why that
        // order matters.
        foreach (var keyword in KeywordFilterList.Arrange(
                     _keywordsInScope.Where(k => Matches(k.Value) && !_selectedKeywords.Contains(k.Value)),
                     MaxKeywordChips))
        {
            KeywordChips.Add(new KeywordFilterChip(keyword.Value, keyword.Count, false, ToggleKeywordFilter));
        }

        OnPropertyChanged(nameof(HasKeywordChips));
        OnPropertyChanged(nameof(KeywordFilterEmptyText));

        RebuildKeywordPills();
    }

    private void ToggleKeywordFilter()
    {
        _selectedKeywords.Clear();

        foreach (var chip in KeywordChips.Where(c => c.IsSelected))
        {
            _selectedKeywords.Add(chip.Name);
        }

        RebuildKeywordPills();

        WriteKeywords();
    }

    private void WriteKeywords() => WriteFilter(() =>
    {
        var chosen = _selectedKeywords.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();

        SearchText = KeywordFilterMatchAll
            // One term each: repeating the field is how the parser is told to require all of them.
            ? SearchQueryText.WithFieldTerms(SearchText, "keyword", chosen)
            : SearchQueryText.WithFieldTerms(
                SearchText, "keyword", chosen.Count == 0 ? [] : [string.Join(',', chosen)]);
    });

    private void WriteKinds() => WriteFilter(() =>
    {
        var kinds = new List<string>();

        if (FilterRaw) kinds.Add("raw");
        if (FilterJpeg) kinds.Add("jpg");
        if (FilterVideo) kinds.Add("video");

        // All three ticked is the same as no filter at all, and says so more plainly.
        var value = kinds.Count is 0 or 3 ? null : string.Join(',', kinds);

        SearchText = SearchQueryText.WithField(SearchText, "type", value);
    });

    private void WriteFlags() => WriteFilter(() =>
    {
        var flags = new List<string>();

        if (FilterAccepted) flags.Add("accepted");
        if (FilterRejected) flags.Add("rejected");
        if (FilterUnflagged) flags.Add("none");

        var value = flags.Count is 0 or 3 ? null : string.Join(',', flags);

        SearchText = SearchQueryText.WithField(SearchText, "flag", value);
    });

    private void WriteFilter(Action write)
    {
        if (_syncingFilters)
        {
            return;
        }

        _syncingFilters = true;

        try
        {
            write();
        }
        finally
        {
            _syncingFilters = false;
        }

        _ = SearchCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Brings the controls in line with the query, so opening the popup shows what is actually being
    /// filtered — including filters that were typed rather than clicked.
    ///
    /// Read by parsing rather than by matching text, so rating:&gt;=3 and r:&gt;=3 and a query where
    /// the term sits in the middle all light the same three stars.
    /// </summary>
    private void ReadFiltersFromQuery()
    {
        if (_syncingFilters)
        {
            return;
        }

        var query = SearchQueryParser.Parse(SearchText);

        _syncingFilters = true;

        try
        {
            var rating = RatingFilterCycle.From(query.Rating);
            FilterRating = rating.Stars;
            FilterRatingExact = rating.Exact;

            var kinds = query.Kinds;
            var all = kinds.IsDefaultOrEmpty;

            FilterRaw = all || kinds.Contains(MediaKind.Raw);
            FilterJpeg = all || kinds.Contains(MediaKind.Jpeg);
            FilterVideo = all || kinds.Contains(MediaKind.Video);

            ReadKeywordsFromQuery(query);

            foreach (var chip in LabelChips)
            {
                chip.SetSelected(query.Labels.Any(l => string.Equals(l, chip.Term, StringComparison.OrdinalIgnoreCase))
                    || (chip.Term == "none" && query.IncludeUnlabelled));
            }

            var flags = query.Flags;
            var everyFlag = flags.IsDefaultOrEmpty;

            FilterAccepted = everyFlag || flags.Contains(MediaFlag.Accepted);
            FilterRejected = everyFlag || flags.Contains(MediaFlag.Rejected);
            FilterUnflagged = everyFlag || flags.Contains(MediaFlag.None);
        }
        finally
        {
            _syncingFilters = false;
        }
    }

    /// <summary>
    /// Ticks the keywords the query asks for, and sets any/all from how they are written.
    ///
    /// <c>k:sand,dust</c> is one term offering alternatives — any. <c>k:sand k:dust</c> is two terms
    /// that both have to match — all. A query mixing the two cannot be shown honestly by a single
    /// switch, so it is read as "all" and the ticks still say which words are involved.
    /// </summary>
    private void ReadKeywordsFromQuery(SearchQuery query)
    {
        _selectedKeywords.Clear();

        foreach (var word in query.Keywords.SelectMany(group => group.AnyOf))
        {
            _selectedKeywords.Add(word);
        }

        KeywordFilterMatchAll = query.Keywords.Length > 1;

        RebuildKeywordChips();
    }

    /// <summary>
    /// Which of the three states the stars are in. Needed because "exactly 3" and "3 and up" fill
    /// the same three stars, so the stars alone cannot say which is meant.
    /// </summary>
    public string RatingFilterSummary => FilterRating switch
    {
        <= 0 => string.Empty,
        _ => FilterRatingExact ? "exactly" : "and up"
    };

    /// <summary>
    /// Clicking a star walks it round: once for "and up", again for "exactly", again to clear.
    /// </summary>
    /// <param name="stars">
    /// Taken as a string because that is what a XAML CommandParameter is; parsing here keeps five
    /// buttons' markup free of x:Int32 wrappers.
    /// </param>
    [RelayCommand]
    private void SetRatingFilter(string? stars)
    {
        if (!int.TryParse(stars, out var clicked))
        {
            return;
        }

        var next = RatingFilterCycle.Next(new RatingFilterState(FilterRating, FilterRatingExact), clicked);

        WriteFilter(() =>
        {
            FilterRating = next.Stars;
            FilterRatingExact = next.Exact;

            SearchText = SearchQueryText.WithField(SearchText, "rating", RatingFilterCycle.ToTerm(next));
        });
    }

    /// <summary>Moves through the offered fields, wrapping at both ends.</summary>
    public void MoveSuggestion(int delta)
    {
        if (FieldSuggestions.Count == 0)
        {
            return;
        }

        var next = SelectedSuggestionIndex + delta;
        SelectedSuggestionIndex = (next % FieldSuggestions.Count + FieldSuggestions.Count) % FieldSuggestions.Count;
    }

    /// <summary>
    /// The selection decoded at native resolution, for the viewer. Null until asked for: it costs
    /// tens of megabytes and a decode, neither of which is worth spending on the inline preview.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoupeSource))]
    [NotifyPropertyChangedFor(nameof(IsLoupeFullResolution))]
    [NotifyPropertyChangedFor(nameof(LoupeTargetWidth))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceLabel))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceTooltip))]
    [NotifyPropertyChangedFor(nameof(CanSwitchPreviewSource))]
    [NotifyPropertyChangedFor(nameof(IsShowingDevelopedRaw))]
    private Bitmap? _fullPreview;

    private CancellationTokenSource? _fullPreviewCts;

    /// <summary>
    /// What has been asked for, delivered, and is held. See <see cref="FullPreviewTracker"/> — this
    /// used to be a single "last requested" field, which latched whenever a run ended without
    /// producing anything and left the picture stuck on its embedded JPEG.
    /// </summary>
    private readonly FullPreviewTracker _fullPreviewState = new();

    /// <summary>
    /// Which decoder produced what is on screen. Only a LibRaw develop answers to the develop
    /// settings, so the panel has to be able to say when it does not.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevelopSettingsApply))]
    [NotifyPropertyChangedFor(nameof(RendererNote))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceLabel))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceTooltip))]
    [NotifyPropertyChangedFor(nameof(CanSwitchPreviewSource))]
    [NotifyPropertyChangedFor(nameof(IsShowingDevelopedRaw))]
    private string? _fullPreviewRenderer;

    public bool DevelopSettingsApply => FullPreviewRenderer is null or DecodedImage.LibRaw;

    /// <summary>
    /// What is actually on screen in the viewer: the demosaiced sensor data, the JPEG the camera
    /// embedded in the RAW, or an ordinary image file.
    ///
    /// Worth stating rather than leaving to be inferred. The two renderings of a RAW can look similar
    /// at a glance and are not remotely the same thing to judge a photograph by, and which one you get
    /// depends on a setting, a keystroke, and whether the develop succeeded.
    /// </summary>
    public string PreviewSourceLabel
    {
        get
        {
            if (FullPreview is null)
            {
                return "Preview";
            }

            if (!IsRawSelected)
            {
                return "Full resolution";
            }

            // A develop is the only thing that sets a renderer. Falling back to the embedded preview
            // goes through the ordinary image path, which does not.
            return FullPreviewRenderer is null ? "JPEG" : "RAW";
        }
    }

    /// <summary>True while the viewer is showing developed sensor data, for the badge to colour by.</summary>
    public bool IsShowingDevelopedRaw => FullPreview is not null && IsRawSelected && FullPreviewRenderer is not null;

    /// <summary>
    /// Whether the badge is worth clicking. Only a RAW has two renderings to move between; for
    /// anything else the badge is a label and nothing more.
    /// </summary>
    public bool CanSwitchPreviewSource => IsRawSelected;

    /// <summary>
    /// What clicking the badge will do, said before it is clicked.
    ///
    /// The badge was already saying which rendering was on screen, and the key that flips it was
    /// discoverable only from the hint strip that fades out. Naming the effect and the key together
    /// means one hover answers both "can I change this?" and "how?".
    /// </summary>
    public string PreviewSourceTooltip => !IsRawSelected
        ? "This is not a RAW file, so there is only one rendering of it."
        : IsShowingDevelopedRaw
            ? "Showing the developed RAW. Click, or press \\, for the JPEG the camera embedded."
            : "Showing the JPEG the camera embedded. Click, or press \\, to develop the RAW.";

    /// <summary>Says why the controls are doing nothing, rather than leaving them looking broken.</summary>
    public string? RendererNote => DevelopSettingsApply
        ? null
        : "This file was rendered by macOS — LibRaw cannot unpack it, so these settings do not apply.";

    /// <summary>
    /// Set while the full-size decode is running. Developing a RAW takes seconds, and without
    /// saying so the viewer looks like it has simply decided the preview is as good as it gets.
    /// </summary>
    [ObservableProperty]
    private bool _isPreparingFullPreview;

    [ObservableProperty]
    private string? _fullPreviewStatus;

    /// <summary>
    /// Decodes the selection at full size if that has not already happened. Safe to call repeatedly;
    /// the viewer calls it whenever the selection changes.
    /// </summary>
    public async Task EnsureFullPreviewAsync()
    {
        // Selecting an item raises several properties, each of which asks the viewer to refresh.
        // Without this the same decode is started and cancelled two or three times, and a RAW
        // develop is far too expensive to do that to.
        var wanted = SelectedItem?.File.FullPath;
        if (!_fullPreviewState.ShouldStart(wanted))
        {
            return;
        }

        // Re-rendering the same photograph is a comparison: the point is to see one version replace
        // the other. Dropping back to the preview in between would put a different, lower-quality
        // image on screen for several seconds, which is exactly what makes the comparison useless.
        var changingFile = _fullPreviewState.IsChangingFile(wanted);

        _fullPreviewState.Begin(wanted);

        if (_fullPreviewCts is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        if (changingFile)
        {
            DiscardFullPreview();
        }

        if (SelectedItem is not { } item || IsVideoSelected)
        {
            // Nothing to decode. The marker has to come off or every later request for this file
            // would be dismissed as already running.
            _fullPreviewState.Ended(wanted);
            return;
        }

        var cts = new CancellationTokenSource();
        _fullPreviewCts = cts;

        var developing = DevelopRawFiles && MediaTypeRegistry.IsRaw(item.File.FullPath);
        FullPreviewStatus = developing ? "Developing RAW…" : "Loading full resolution…";
        IsPreparingFullPreview = true;

        try
        {
            var decoded = await _fullImages.DecodeAsync(item.File, cts.Token).ConfigureAwait(true);

            // The selection can move while a 24MP file is decoding; a late arrival must not
            // replace what is now on screen.
            if (decoded is null || cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            // Swapped, then the old one released: assigning first means the UI is already showing
            // the new bitmap before the previous one is disposed.
            var replaced = FullPreview;
            FullPreview = ToBitmap(decoded);
            FullPreviewRenderer = decoded.Renderer;
            _fullPreviewState.Delivering(item.File.FullPath);

            if (replaced is not null)
            {
                Dispatcher.UIThread.Post(replaced.Dispose, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {File} at full size", item.File.FullPath);
        }
        finally
        {
            // Only the run that is still current may clear the indicator; an older one finishing
            // late must not hide the wait for the image now on screen.
            if (ReferenceEquals(_fullPreviewCts, cts))
            {
                IsPreparingFullPreview = false;
            }

            // Unconditionally, and whatever happened above: a run that returned early, threw, or was
            // cancelled must not leave this file looking like one already being worked on.
            _fullPreviewState.Ended(wanted);
        }
    }

    /// <summary>
    /// Switches between developing RAW files and showing their embedded preview. Bound to \ in the
    /// viewer, the way Lightroom uses it to flip between two renderings of the same shot.
    /// </summary>
    [RelayCommand]
    private void ToggleRawDevelopment() => DevelopRawFiles = !DevelopRawFiles;

    /// <summary>
    /// Gives up the full-size bitmap and forgets that it was ever asked for, so a later request
    /// decodes again rather than being swallowed by the guard.
    ///
    /// What the viewer calls when it closes. It cannot simply discard: the loupe in the main window
    /// wants the same bitmap, and leaving the request recorded would leave the loupe stuck on the
    /// preview for as long as that file stayed selected.
    /// </summary>
    public void ReleaseFullPreview()
    {
        DiscardFullPreview();
        InvalidateFullPreviewRequest();
    }

    /// <summary>
    /// Starts the full-resolution decode if it is cheap, so the loupe is sharp the moment it opens.
    ///
    /// Only for formats that decode quickly. Developing a RAW takes seconds and hundreds of megabytes,
    /// which is not worth spending on every photograph passed over on the way to somewhere else — for
    /// those the loupe asks when it is actually opened, and says what it is doing while it waits.
    /// </summary>
    private void PrepareLoupeSource()
    {
        var wanted = SelectedItem?.File.FullPath;

        // A full-size bitmap of the photograph we have just moved away from must go, or the loupe
        // would magnify the wrong picture. Skipped when a decode for this file is already in flight,
        // which is the case whenever the viewer window is open — clearing the guard there would
        // start a second develop of the same RAW.
        if (_fullPreviewState.InFlight != wanted && _fullPreviewState.Held != wanted)
        {
            ReleaseFullPreview();
        }

        if (SelectedItem is { } item
            && !IsVideoSelected
            && !MediaTypeRegistry.IsRaw(item.File.FullPath))
        {
            _ = EnsureFullPreviewAsync();
        }
    }

    /// <summary>Releases the full-size bitmap, which is far too large to keep around unseen.</summary>
    public void DiscardFullPreview()
    {
        FullPreview?.Dispose();
        FullPreview = null;
        _fullPreviewState.Forget();
    }

    /// <summary>
    /// Forgets what was last asked for, so the next request re-runs even for the same file. Needed
    /// when the *way* it is decoded changes rather than which file it is.
    /// </summary>
    private void InvalidateFullPreviewRequest() => _fullPreviewState.Invalidate();

    private static Bitmap ToBitmap(DecodedImage image)
    {
        var size = new PixelSize(image.Width, image.Height);
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var locked = bitmap.Lock())
        {
            var stride = image.Width * 4;

            if (locked.RowBytes == stride)
            {
                System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, locked.Address, image.Pixels.Length);
            }
            else
            {
                // Rows can be padded for alignment, so copy row by row rather than assuming a match.
                for (var row = 0; row < image.Height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        image.Pixels, row * stride, locked.Address + (row * locked.RowBytes), stride);
                }
            }
        }

        return bitmap;
    }

    /// <summary>
    /// True to develop RAW files for viewing, false to show the JPEG the camera embedded.
    /// Changing it reloads whatever is on screen, so the effect is visible immediately.
    /// </summary>
    [ObservableProperty]
    private bool _developRawFiles = true;

    partial void OnDevelopRawFilesChanged(bool value) => _ = ApplyDevelopRawFilesAsync(value);

    /// <summary>
    /// Saves the choice and re-renders what is on screen with it.
    ///
    /// The save has to be awaited. The decoder reads this from the settings service, and the service
    /// only publishes a new value once it has finished writing the file — so starting the decode
    /// without waiting raced the write and, about half the time, developed the file again with the
    /// value the toggle had just replaced. That was the "\ does not consistently toggle" bug: the
    /// picture and its badge would keep describing the old rendering while the controls showed the
    /// new one.
    ///
    /// This is what <see cref="SaveAndReloadDevelopAsync"/> already did for the develop settings; the
    /// two now behave the same way.
    /// </summary>
    private async Task ApplyDevelopRawFilesAsync(bool value)
    {
        await _settings.SaveAsync(_settings.Current with { DevelopRawFiles = value }).ConfigureAwait(true);

        // Same file, different rendering: the request guard has to be cleared or nothing happens.
        InvalidateFullPreviewRequest();
        await EnsureFullPreviewAsync().ConfigureAwait(true);
    }

    // ---- RAW develop controls -------------------------------------------------------------

    public static IReadOnlyList<RawHighlightMode> HighlightModes { get; } = Enum.GetValues<RawHighlightMode>();

    public static IReadOnlyList<RawWhiteBalance> WhiteBalanceModes { get; } = Enum.GetValues<RawWhiteBalance>();

    public static IReadOnlyList<RawNoiseReduction> NoiseReductionLevels { get; } = Enum.GetValues<RawNoiseReduction>();

    public static IReadOnlyList<RawQuality> RawQualities { get; } = Enum.GetValues<RawQuality>();

    [ObservableProperty]
    private RawHighlightMode _highlights;

    [ObservableProperty]
    private RawWhiteBalance _whiteBalance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureDisplay))]
    private double _exposureStops;

    [ObservableProperty]
    private RawNoiseReduction _noiseReduction;

    [ObservableProperty]
    private RawQuality _rawQuality;

    /// <summary>Signed, because "+1 stop" and "1 stop" read differently at a glance.</summary>
    public string ExposureDisplay => Math.Abs(ExposureStops) < 0.01
        ? "As shot"
        : $"{ExposureStops:+0.0;-0.0} EV";

    /// <summary>True when the develop is untouched, so a Reset can be offered only when it does something.</summary>
    public bool HasDevelopAdjustments => !CurrentDevelop.IsDefault;

    /// <summary>Whether the develop controls apply to what is on screen.</summary>
    public bool IsRawSelected =>
        SelectedItem is { } item && MediaTypeRegistry.IsRaw(item.File.FullPath);

    private RawDevelopSettings CurrentDevelop => new()
    {
        Highlights = Highlights,
        WhiteBalance = WhiteBalance,
        ExposureStops = ExposureStops,
        NoiseReduction = NoiseReduction,
        Quality = RawQuality
    };

    partial void OnHighlightsChanged(RawHighlightMode value) => ApplyDevelopChange();

    partial void OnWhiteBalanceChanged(RawWhiteBalance value) => ApplyDevelopChange();

    partial void OnNoiseReductionChanged(RawNoiseReduction value) => ApplyDevelopChange();

    partial void OnRawQualityChanged(RawQuality value) => ApplyDevelopChange();

    // Dragged rather than picked, so it waits for the dragging to stop.
    partial void OnExposureStopsChanged(double value) => ApplyDevelopChange(debounce: true);

    [RelayCommand]
    private void ResetDevelop()
    {
        var defaults = RawDevelopSettings.Default;

        // Assigned through the properties so the UI follows, but the reload is left until the end
        // rather than firing once per field.
        _suppressDevelopReload = true;
        Highlights = defaults.Highlights;
        WhiteBalance = defaults.WhiteBalance;
        ExposureStops = defaults.ExposureStops;
        NoiseReduction = defaults.NoiseReduction;
        _suppressDevelopReload = false;

        ApplyDevelopChange();
    }

    private bool _suppressDevelopReload;
    private CancellationTokenSource? _developDebounceCts;

    /// <summary>
    /// Saves the develop settings and re-renders what is on screen.
    ///
    /// Debounced for anything dragged: a develop takes seconds, and starting one per slider tick
    /// would queue work faster than it could be cancelled.
    /// </summary>
    private void ApplyDevelopChange(bool debounce = false)
    {
        if (_suppressDevelopReload)
        {
            return;
        }

        OnPropertyChanged(nameof(HasDevelopAdjustments));

        _developDebounceCts?.Cancel();
        _developDebounceCts?.Dispose();
        _developDebounceCts = null;

        if (!debounce)
        {
            _ = SaveAndReloadDevelopAsync();
            return;
        }

        var cts = new CancellationTokenSource();
        _developDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(SaveAndReloadDevelopAsync);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task SaveAndReloadDevelopAsync()
    {
        await _settings.SaveAsync(_settings.Current with { RawDevelop = CurrentDevelop }).ConfigureAwait(true);

        // Same file, different rendering: the request guard has to be cleared or nothing happens.
        InvalidateFullPreviewRequest();
        await EnsureFullPreviewAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// True to open the viewer in real fullscreen, false to open it maximised on the current
    /// screen. Persisted, because it is a working preference rather than a per-item choice.
    /// </summary>
    [ObservableProperty]
    private bool _viewerOpensFullscreen;

    partial void OnViewerOpensFullscreenChanged(bool value)
        => _ = _settings.SaveAsync(_settings.Current with { ViewerOpensFullscreen = value });

    /// <summary>
    /// Whether the folders panel is open. The column width follows in the view, which owns it: the
    /// splitter writes to that width as it is dragged and a binding would be fighting it.
    ///
    /// Collapsing is the panel's width going to nothing, not the tree being hidden — a hidden child
    /// of a fixed-width column would leave its gap behind.
    /// </summary>
    [ObservableProperty]
    private bool _isFolderPanelVisible = true;

    partial void OnIsFolderPanelVisibleChanged(bool value)
        => RememberLayout(l => l with { FolderPanelCollapsed = !value });

    /// <summary>
    /// The same for the inspector. Worth collapsing for the same reason and more often: culling a
    /// shoot needs the picture large and nothing else, and the panel is the widest thing competing
    /// with it.
    /// </summary>
    [ObservableProperty]
    private bool _isInspectorVisible = true;

    partial void OnIsInspectorVisibleChanged(bool value)
        => RememberLayout(l => l with { InspectorCollapsed = !value });

    /// <summary>
    /// Whether the preview sits above the thumbnails rather than below them.
    ///
    /// Both readings are legitimate: thumbnails first is a contact sheet being worked through,
    /// preview first is one photograph with the shoot underneath, which is how most editing
    /// applications are laid out. Whichever is chosen is remembered.
    /// </summary>
    [ObservableProperty]
    private bool _previewOnTop;

    partial void OnPreviewOnTopChanged(bool value)
    {
        RememberLayout(l => l with { PreviewOnTop = value });

        // The pane moved out from under the selection; put it back in sight.
        if (SelectedItem is not null)
        {
            ScrollSelectionIntoView?.Invoke(SelectedItem);
        }
    }

    [RelayCommand]
    private void ToggleInspectorPanel() => IsInspectorVisible = !IsInspectorVisible;

    /// <summary>
    /// Mirrors the contact sheet experiment, which is set in Settings. Only the dividers care: they
    /// are a line normally and a film rebate under the contact sheet, and the view owns their width
    /// rather than binding it to the resource.
    /// </summary>
    [ObservableProperty]
    private bool _contactSheet;

    /// <summary>
    /// Moves the selection through the grid, for arrow-key browsing in the fullscreen viewer.
    /// Stops at the ends rather than wrapping: silently looping back to the first file gives no
    /// clue that the last one was reached.
    /// </summary>
    [RelayCommand]
    private void SelectNext() => MoveSelection(1);

    [RelayCommand]
    private void SelectPrevious() => MoveSelection(-1);

    private void MoveSelection(int delta)
    {
        if (VisibleItems.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : VisibleItems.IndexOf(SelectedItem);
        var target = Math.Clamp(index + delta, 0, VisibleItems.Count - 1);

        if (target != index)
        {
            SelectedItem = VisibleItems[target];
        }
    }

    [RelayCommand]
    private void ToggleFolderPanel() => IsFolderPanelVisible = !IsFolderPanelVisible;

    /// <summary>
    /// Most recently opened first. Kept in step with the persisted list so the Open Recent menu can
    /// be rebuilt from it.
    /// </summary>
    public ObservableCollection<string> RecentWorkspaces { get; }

    /// <summary>
    /// Widens search past the workspace to the whole catalog. Off by default: a workspace that
    /// returned results from unrelated folders would not be much of a workspace.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private bool _searchEverywhere;

    partial void OnSearchEverywhereChanged(bool value)
    {
        if (IsShowingSearchResults)
        {
            _ = SearchAsync();
        }

        // The scope radio buttons sit in the same popup as the keyword list, so the list has to
        // follow them — otherwise widening the scope leaves it offering the narrower set.
        _ = EnsureKeywordsLoadedAsync();
    }

    /// <summary>
    /// Shown in the preview pane only while a video is selected, so a missing FFmpeg stays out of
    /// the way during metadata work but is unmissable the moment someone wants to watch something.
    /// </summary>
    [ObservableProperty]
    private bool _showFfmpegNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [NotifyPropertyChangedFor(nameof(WriteAllPrompt))]
    [NotifyPropertyChangedFor(nameof(DiscardAllPrompt))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    [ObservableProperty]
    private bool _isWritingAll;

    /// <summary>
    /// Whether the status bar is asking about one of its two bulk actions.
    ///
    /// Both sit next to the count of pending changes, which is the one thing in the bar worth
    /// clicking, and "Discard all" throws away every unsaved judgement in the session with no way
    /// back. Asked in place rather than in a dialog, as the destructive buttons in Settings do: the
    /// question appears where the button was, so the answer is where the hand already is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingBulkAction))]
    private bool _isConfirmingWriteAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingBulkAction))]
    private bool _isConfirmingDiscardAll;

    public bool IsConfirmingBulkAction => IsConfirmingWriteAll || IsConfirmingDiscardAll;

    public string WriteAllPrompt => $"Write {PendingChangeCount} file(s) to sidecars?";

    public string DiscardAllPrompt => $"Discard the changes to {PendingChangeCount} file(s)?";

    [RelayCommand]
    private void BeginWriteAll()
    {
        IsConfirmingDiscardAll = false;
        IsConfirmingWriteAll = true;
    }

    [RelayCommand]
    private void BeginDiscardAll()
    {
        IsConfirmingWriteAll = false;
        IsConfirmingDiscardAll = true;
    }

    [RelayCommand]
    private void CancelBulkAction()
    {
        IsConfirmingWriteAll = false;
        IsConfirmingDiscardAll = false;
    }

    /// <summary>
    /// A question about changes that no longer exist has no answer. Writing the last one from the
    /// inspector while the bar was asking would otherwise leave the question waiting to reappear
    /// over the next edit.
    /// </summary>
    partial void OnPendingChangeCountChanged(int value)
    {
        if (value == 0)
        {
            IsConfirmingWriteAll = false;
            IsConfirmingDiscardAll = false;
        }
    }

    /// <summary>
    /// Shown over the grid whenever it has nothing in it. There are three quite different reasons
    /// for an empty grid — nothing opened yet, a folder with no readable media, and a search that
    /// matched nothing — and each needs its own explanation to avoid looking like a failure.
    /// </summary>
    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private string _emptyStateTitle = string.Empty;

    [ObservableProperty]
    private string _emptyStateDetail = string.Empty;

    /// <summary>Only the opening prompt offers an action; the other two are explanations.</summary>
    [ObservableProperty]
    private bool _showEmptyStateAction;

    private void UpdateEmptyState()
    {
        // Scanning has its own progress indicator, and flashing "no media here" for the moment
        // before the first results arrive would be wrong as often as it is right.
        ShowEmptyState = VisibleItems.Count == 0 && !IsScanning;
        if (!ShowEmptyState)
        {
            return;
        }

        if (IsShowingSearchResults)
        {
            EmptyStateTitle = "No matches";
            EmptyStateDetail = !SearchEverywhere && HasWorkspace
                ? $"Nothing in {WorkspaceName} matched. Search only finds files that have been "
                  + "indexed, and folders are indexed as you browse them. Turn on Search everywhere "
                  + "to look outside this workspace."
                : "Search only finds files that have been indexed. Folders are indexed in the "
                  + "background as you browse them, so open a folder once and its contents become "
                  + "searchable.";
            ShowEmptyStateAction = false;
        }
        else if (!HasWorkspace)
        {
            EmptyStateTitle = "No workspace open";
            EmptyStateDetail = "Open a folder to work in. It becomes the root of the tree, and "
                               + "searches are scoped to it.";
            ShowEmptyStateAction = true;
        }
        else
        {
            EmptyStateTitle = "Nothing to show here";
            EmptyStateDetail = Recursive
                ? "This folder and its subfolders contain no images or video BetterDAM can read."
                : "This folder contains no images or video BetterDAM can read. Turn on Recursive to "
                  + "include its subfolders.";
            ShowEmptyStateAction = false;
        }
    }

    partial void OnIsScanningChanged(bool value) => UpdateEmptyState();

    partial void OnCurrentFolderPathChanged(string? value) => UpdateEmptyState();

    partial void OnIsShowingSearchResultsChanged(bool value) => UpdateEmptyState();

    partial void OnWorkspacePathChanged(string? value) => UpdateEmptyState();

    /// <summary>
    /// Writes every pending edit to its XMP sidecar. Files are processed one at a time and failures
    /// are counted rather than aborting the run, so one unwritable file does not strand the rest.
    /// The media files themselves are never touched.
    /// </summary>
    [RelayCommand]
    private async Task WriteAllPendingSidecarsAsync()
    {
        IsConfirmingWriteAll = false;

        if (!_writer.IsAvailable || _pending.Count == 0)
        {
            return;
        }

        var pending = _pending.GetAll();
        var byPath = MediaItems.ToDictionary(i => i.File.FullPath, StringComparer.Ordinal);

        IsWritingAll = true;
        var written = 0;
        var failed = 0;
        var saved = new List<MediaFile>();

        try
        {
            foreach (var change in pending)
            {
                if (!byPath.TryGetValue(change.FilePath, out var item))
                {
                    continue;
                }

                StatusText = $"Writing sidecars — {written + failed + 1} of {pending.Count}";

                var result = await _writer.WriteSidecarAsync(item.File, change.Edited, new SidecarWriteOptions());
                if (result.Success)
                {
                    // Before the discard: dropping the change is what sends the tile back to the
                    // catalog's older reading, and the re-index that would correct it is minutes of
                    // files away.
                    _catalogMarks[change.FilePath] = new MediaMarks(
                        change.Edited.Rating,
                        change.Edited.Flag ?? MediaFlag.None,
                        change.Edited.Label);

                    _pending.Discard(change.FilePath);
                    item.HasPendingChanges = false;
                    item.HasSidecar = true;
                    saved.Add(item.File);
                    written++;
                }
                else
                {
                    _logger.LogWarning("Sidecar write failed for {File}: {Error}", change.FilePath, result.Error);
                    failed++;
                }
            }

            StatusText = failed == 0
                ? $"Wrote {written} XMP sidecar(s). Original media untouched."
                : $"Wrote {written} sidecar(s), {failed} failed — see the log for details.";
        }
        finally
        {
            IsWritingAll = false;

            if (SelectedItem is { } selected)
            {
                await Inspector.LoadAsync(selected);
            }
        }

        // The catalog is a cache of what is in the files, and the files have just changed. The
        // sidecar timestamp would catch this on the next scan anyway; doing it now means a search
        // run straight after saving agrees with what was just saved, rather than a folder later.
        if (saved.Count > 0)
        {
            await RunIndexAsync(saved, CancellationToken.None);
        }
    }

    /// <summary>
    /// Fills in the ratings, flags and labels the grid draws, for whatever is listed now.
    ///
    /// One query for the folder rather than one per tile, and safe to call again — it is how the
    /// grid catches up after indexing finishes, after a save, and after the label library changes
    /// colour underneath it.
    /// </summary>
    private async Task LoadMarksAsync()
    {
        if (MediaItems.Count == 0)
        {
            return;
        }

        try
        {
            // The listed files may span the whole catalog when showing search results, so the scope
            // is the workspace rather than the folder being browsed.
            // Copied rather than held as returned: a save updates the entry for the file it wrote,
            // which is then the saved value for as long as this reading stands.
            _catalogMarks = new Dictionary<string, MediaMarks>(
                await _catalog.GetMarksAsync(SearchEverywhere ? null : WorkspacePath).ConfigureAwait(true),
                StringComparer.OrdinalIgnoreCase);

            RedrawMarks();
        }
        catch (Exception ex)
        {
            // Tiles without their marks are a smaller loss than a folder that will not open.
            _logger.LogDebug(ex, "Could not read marks for the grid");
        }
    }

    /// <summary>
    /// What the catalog last said. Kept so an edit can be undone back to it: a discarded change has
    /// to reveal the saved value again, and re-querying for one tile would be absurd.
    /// </summary>
    private Dictionary<string, MediaMarks> _catalogMarks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes in what a save has just put on disk: the tile it was saved from, and the catalog, which
    /// answers searches about it.
    ///
    /// The panel discards the pending change once the sidecar is written, and until this the only
    /// thing left to draw the tile from was the catalog's last reading — taken before the edit. So
    /// rating a raw file and saving it made the stars vanish, and they came back on reopening the
    /// folder, which is where the file is read again. The marks are corrected at once because the
    /// tile is on screen now, and the file is re-read afterwards so the catalog agrees too: a search
    /// run straight after a save should find what was saved.
    /// </summary>
    private void OnSidecarWritten(MediaFile file, EditableMetadata written)
    {
        _catalogMarks[file.FullPath] =
            new MediaMarks(written.Rating, written.Flag ?? MediaFlag.None, written.Label);

        RedrawMarksFor(file.FullPath);

        _ = RunIndexAsync([file], CancellationToken.None);
    }

    /// <summary>
    /// What a tile should show: the unsaved edit if there is one, otherwise what is on disk.
    ///
    /// Pending wins because the grid and the inspector are looking at the same file, and a tile
    /// still showing three stars while the inspector shows four is the kind of disagreement that
    /// makes people distrust both.
    /// </summary>
    private MediaMarks MarksFor(string path)
        => _pending.GetEdited(path) is { } edited
            ? new MediaMarks(edited.Rating, edited.Flag ?? MediaFlag.None, edited.Label)
            : _catalogMarks.TryGetValue(path, out var found) ? found : MediaMarks.None;

    private void SetMarks(MediaItemViewModel item)
    {
        var marks = MarksFor(item.File.FullPath);

        item.Marks = marks;
        item.LabelColour = LabelColours.Resolve(_settings.Current.Labels, marks.Label);
    }

    /// <summary>Re-resolves every tile. Cheap — no query, just the label colours and a few flags.</summary>
    private void RedrawMarks()
    {
        foreach (var item in MediaItems)
        {
            SetMarks(item);
        }
    }

    /// <summary>One tile, for when a single file is edited.</summary>
    private void RedrawMarksFor(string? path)
    {
        if (path is null)
        {
            // A batch edit or a discard-all: which files changed is not said, so redraw the lot.
            RedrawMarks();
            return;
        }

        foreach (var item in MediaItems)
        {
            if (string.Equals(item.File.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                SetMarks(item);
                return;
            }
        }
    }

    /// <summary>
    /// How long to leave between refreshes prompted by the window coming forward.
    ///
    /// Alt-tabbing back and forth raises the event repeatedly, and a folder of fifty thousand files
    /// does not want re-stating each time. Long enough to absorb that, short enough that the answer
    /// to "I edited it a moment ago" is still yes.
    /// </summary>
    private static readonly TimeSpan ExternalRefreshInterval = TimeSpan.FromSeconds(2);

    private DateTimeOffset _lastExternalRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// Picks up edits made in another application, when this window comes back to the front.
    ///
    /// This is how Unity's editor behaves and for the same reason: watching a whole tree is
    /// expensive and unreliable across platforms, while the moment a window regains focus is both
    /// cheap to detect and exactly when a stale view starts to matter. The comparison itself is the
    /// one the indexer already does — size, modified time and the sidecar's timestamp — so a folder
    /// where nothing has changed costs a stat per file and nothing else.
    /// </summary>
    public async Task RefreshExternalChangesAsync()
    {
        if (MediaItems.Count == 0 || IsScanning || IsIndexing || IsWritingAll
            || _indexingWorkspace || _workspaceIndexPending)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastExternalRefresh < ExternalRefreshInterval)
        {
            return;
        }

        _lastExternalRefresh = DateTimeOffset.UtcNow;

        var listed = MediaItems.Select(item => item.File.FullPath).ToList();

        // Re-read from disk rather than trusting what the grid holds: those values were taken when
        // the folder was scanned, and are precisely what an external edit makes wrong.
        var fresh = await Task.Run(() =>
        {
            var files = new List<MediaFile>(listed.Count);

            foreach (var path in listed)
            {
                try
                {
                    var info = new FileInfo(path);

                    if (info.Exists)
                    {
                        files.Add(MediaFile.FromFileInfo(info));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable file should not stop the rest from being brought up to date.
                    _logger.LogDebug(ex, "Could not stat {Path} while refreshing", path);
                }
            }

            return files;
        }).ConfigureAwait(true);

        if (fresh.Count == 0)
        {
            return;
        }

        // Only what actually changed is read; RunIndexAsync refreshes the grid's marks when it is
        // done. The file list itself is left alone — rebuilding it would throw away the selection
        // and the scroll position every time the window was clicked on.
        await RunIndexAsync(fresh, CancellationToken.None);

        // The panel is showing one file in detail, and it holds its own copy of the metadata.
        if (SelectedItem is { } selected)
        {
            await Inspector.LoadAsync(selected);
        }
    }

    /// <summary>Called after the sync dialog closes: it clears whatever it committed.</summary>
    public void RefreshAfterSync()
    {
        PendingChangeCount = _pending.Count;

        foreach (var item in MediaItems)
        {
            item.HasPendingChanges = _pending.HasChanges(item.File.FullPath);
        }

        _ = LoadMarksAsync();

        if (SelectedItem is { } selected)
        {
            _ = Inspector.LoadAsync(selected);
        }
    }

    /// <summary>
    /// After the Changes review closes: the grid's badges follow the store, and any sidecars the
    /// review wrote are re-indexed so a search run straight afterwards agrees with the disk.
    /// </summary>
    public async Task RefreshAfterReviewAsync(IReadOnlyList<MediaFile> written)
    {
        RefreshAfterSync();

        if (written.Count == 0)
        {
            return;
        }

        var paths = written.Select(f => f.FullPath).ToHashSet(StringComparer.Ordinal);
        foreach (var item in MediaItems)
        {
            if (paths.Contains(item.File.FullPath))
            {
                item.HasSidecar = true;
            }
        }

        await RunIndexAsync(written, CancellationToken.None);
    }

    [RelayCommand]
    private void DiscardAllPendingChanges()
    {
        IsConfirmingDiscardAll = false;

        _pending.DiscardAll();

        foreach (var item in MediaItems)
        {
            item.HasPendingChanges = false;
        }

        _ = Inspector.LoadAsync(SelectedItem);
    }

    public static string FfmpegInstallHint => OperatingSystem.IsMacOS()
        ? "Install it with:  brew install ffmpeg"
        : OperatingSystem.IsWindows()
            ? "Install it with:  winget install Gyan.FFmpeg"
            : "Install it with your package manager, e.g.  sudo apt install ffmpeg";

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value is null || value.IsPlaceholder)
        {
            return;
        }

        _ = ScanFolderAsync(value.FullPath);
    }

    partial void OnSelectedItemChanged(MediaItemViewModel? value)
    {
        ShowFfmpegNotice = value is { IsVideo: true } && !_ffmpeg.IsAvailable;
        IsVideoSelected = value is { IsVideo: true } && _ffmpeg.IsAvailable;
        OnPropertyChanged(nameof(IsRawSelected));
        OnPropertyChanged(nameof(PreviewSourceLabel));
        OnPropertyChanged(nameof(IsShowingDevelopedRaw));
        OnPropertyChanged(nameof(PreviewSourceTooltip));
        OnPropertyChanged(nameof(CanSwitchPreviewSource));

        // A video is handed to the player; only stills use the static image preview, so the two
        // never fight over the same pane.
        _ = Player.LoadAsync(IsVideoSelected ? value!.File : null);
        _ = LoadPreviewAsync(IsVideoSelected ? null : value);
        _ = Inspector.LoadAsync(value);
    }

    [ObservableProperty]
    private bool _isVideoSelected;

    [ObservableProperty]
    private string? _searchText;

    /// <summary>
    /// Keeps the filter controls showing what the query actually says, however it was arrived at —
    /// typed, pasted, or clicked.
    /// </summary>
    partial void OnSearchTextChanged(string? value) => ReadFiltersFromQuery();

    /// <summary>True while showing search results rather than a folder's contents.</summary>
    [ObservableProperty]
    private bool _isShowingSearchResults;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private double _indexProgress;

    [ObservableProperty]
    private string? _indexStatus;

    [ObservableProperty]
    private string? _searchWarning;

    /// <summary>
    /// Runs the search. Results come from the catalog, so this searches everything ever indexed
    /// rather than only the folder on screen — which is the entire point of having a catalog.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ClearSearch();
            return;
        }

        if (_searchCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var query = SearchQueryParser.Parse(SearchText);
        SearchWarning = query.UnrecognisedTerms.IsDefaultOrEmpty
            ? null
            : $"Ignored: {string.Join(", ", query.UnrecognisedTerms)}";

        if (query.IsEmpty)
        {
            StatusText = "Enter something to search for.";
            return;
        }

        try
        {
            var scope = SearchEverywhere ? null : WorkspacePath;
            var hits = await _catalog.SearchAsync(query, scope, cancellationToken: cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await CancelActiveScanAsync();

            MediaItems.Clear();
            SelectedItem = null;
            IsShowingSearchResults = true;

            foreach (var hit in hits)
            {
                MediaItems.Add(new MediaItemViewModel(hit.ToMediaFile(), _thumbnails)
                {
                    HasPendingChanges = _pending.HasChanges(hit.FullPath)
                });
            }

            await LoadMarksAsync();

            CurrentFolderPath = $"Search: {SearchText}";

            var where = scope is null ? "everywhere" : WorkspaceName ?? "this workspace";
            StatusText = hits.Count == 0
                ? $"No matches in {where}."
                : $"{hits.Count:N0} match(es) in {where}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Query}", SearchText);
            StatusText = $"Search failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = null;
        SearchWarning = null;
        IsShowingSearchResults = false;

        if (SelectedFolder is { IsPlaceholder: false } folder)
        {
            _ = ScanFolderAsync(folder.FullPath);
            return;
        }

        // Nothing to fall back to. Leaving the hits on screen with an empty search box would
        // present them as a folder listing, and CurrentFolderPath still holds "Search: ..." rather
        // than a real path.
        MediaItems.Clear();
        SelectedItem = null;
        CurrentFolderPath = null;
    }

    [RelayCommand]
    private void CancelIndexing() => _indexCts?.Cancel();

    /// <summary>
    /// Every media file in the workspace, held between offering to index and the answer, so saying
    /// yes does not have to walk the tree again.
    /// </summary>
    private IReadOnlyList<MediaFile> _workspaceFiles = [];

    /// <summary>
    /// Set while the whole workspace is being indexed. Browsing a subfolder must not cancel that
    /// run — the workspace pass already covers those files.
    /// </summary>
    private bool _indexingWorkspace;

    /// <summary>
    /// Set from opening a workspace until the workspace pass has decided what to do. Without it the
    /// initial folder scan would index the top folder, only for the workspace pass to walk the same
    /// files again moments later.
    /// </summary>
    private bool _workspaceIndexPending;

    [ObservableProperty]
    private bool _showIndexPrompt;

    [ObservableProperty]
    private string _indexPromptText = string.Empty;

    /// <summary>
    /// True once indexing has been declined for this workspace, so there is still a way back in
    /// without reopening the folder.
    /// </summary>
    [ObservableProperty]
    private bool _canIndexWorkspace;

    /// <summary>
    /// Walks the whole workspace and decides whether to index it now or ask first. Runs after the
    /// initial scan so the grid fills before any of this starts.
    /// </summary>
    private async Task PrepareWorkspaceIndexAsync(string workspace, CancellationToken cancellationToken)
    {
        ShowIndexPrompt = false;
        CanIndexWorkspace = false;
        _workspaceFiles = [];

        try
        {
            var files = new List<MediaFile>();

            // Always recursive: the Recursive toggle controls what is *shown*, but the workspace is
            // the whole tree, and a search that only covered the top folder would be a poor promise.
            await foreach (var file in _scanner.ScanAsync(
                               workspace,
                               new ScanOptions { Recursive = true },
                               cancellationToken: cancellationToken))
            {
                files.Add(file);
            }

            if (files.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _workspaceFiles = files;

            var choice = _settings.Current.WorkspaceIndexing.TryGetValue(workspace, out var stored)
                ? stored
                : (bool?)null;

            if (choice == false)
            {
                // Declined before. Stay quiet, but leave the door open.
                CanIndexWorkspace = true;
                return;
            }

            if (choice is null && files.Count > AppSettings.IndexPromptThreshold)
            {
                IndexPromptText =
                    $"{files.Count:N0} files in this workspace. Index them so you can search "
                    + "titles, keywords, ratings and camera details? Browsing works either way.";
                ShowIndexPrompt = true;
                return;
            }

            await IndexWorkspaceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate the workspace at {Path}", workspace);
        }
        finally
        {
            _workspaceIndexPending = false;
        }
    }

    [RelayCommand]
    private async Task AcceptIndexPromptAsync()
    {
        ShowIndexPrompt = false;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, true));
        }

        await IndexWorkspaceAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task DeclineIndexPromptAsync()
    {
        ShowIndexPrompt = false;
        CanIndexWorkspace = true;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, false));
        }
    }

    /// <summary>Indexes every file in the workspace. Also the "changed my mind" entry point.</summary>
    [RelayCommand]
    private async Task IndexWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (_workspaceFiles.Count == 0)
        {
            return;
        }

        ShowIndexPrompt = false;
        CanIndexWorkspace = false;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, true));
        }

        _indexingWorkspace = true;
        try
        {
            await RunIndexAsync(_workspaceFiles, cancellationToken);
        }
        finally
        {
            _indexingWorkspace = false;
        }
    }

    /// <summary>
    /// Indexes the files just scanned, in the background. Indexing reads metadata for every file,
    /// so it must never hold up browsing.
    /// </summary>
    private Task IndexScannedAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        // The workspace pass covers every file beneath the root, so browsing into a subfolder while
        // it is running or pending must not cancel it or duplicate its work. CanIndexWorkspace
        // means indexing was declined for this workspace, which per-folder indexing would override.
        if (items.Count == 0 || _indexingWorkspace || _workspaceIndexPending || CanIndexWorkspace)
        {
            return Task.CompletedTask;
        }

        return RunIndexAsync(items.Select(i => i.File).ToList(), CancellationToken.None);
    }

    private async Task RunIndexAsync(IReadOnlyList<MediaFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_indexCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _indexCts = cts;

        IsIndexing = true;
        IndexProgress = 0;

        try
        {
            var progress = new Progress<JobProgress>(p =>
            {
                IndexProgress = p.Fraction;
                IndexStatus = $"Indexing {p.Completed:N0} of {p.Total:N0}";
            });

            var result = await _indexer.IndexAsync(files, progress, cts.Token);

            // Distinguishing the two is worth a few words: "0 files" after opening a large
            // workspace reads as a failure, where "all 48,213 already current" reads as fast.
            IndexStatus = result switch
            {
                { Indexed: 0, Skipped: 0 } => null,
                { Indexed: 0 } => $"All {result.Skipped:N0} file(s) already indexed",
                { Skipped: 0 } => $"Indexed {result.Indexed:N0} file(s)",
                _ => $"Indexed {result.Indexed:N0} file(s), {result.Skipped:N0} already current"
            };
        }
        catch (OperationCanceledException)
        {
            // Chunks are committed as they go, so whatever finished is kept and the next run
            // skips it. Say so, rather than implying the work was lost.
            IndexStatus = "Indexing stopped — progress so far is kept";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Indexing failed");
            IndexStatus = null;
        }
        finally
        {
            IsIndexing = false;
        }

        // Indexing is what puts the marks in the catalog in the first place, so the grid can only
        // draw them once it has finished.
        await LoadMarksAsync();
    }

    /// <summary>True once more than one file is selected, which swaps the inspector for batch mode.</summary>
    [ObservableProperty]
    private bool _isMultiSelection;

    /// <summary>
    /// Called by the view when the grid selection changes. Multi-selection lives here rather than
    /// in a two-way SelectedItems binding, which Avalonia does not make reliable.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<MediaItemViewModel> items)
    {
        Batch.SetSelection(items);
        IsMultiSelection = items.Count > 1;
    }

    partial void OnRecursiveChanged(bool value)
    {
        if (CurrentFolderPath is { } path)
        {
            _ = ScanFolderAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open media folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await OpenPathAsync(path);
    }

    /// <summary>
    /// Opens <paramref name="path"/> as the workspace: it becomes the only root of the tree, the
    /// scope searches are restricted to, and what the application reopens next launch.
    ///
    /// Replacing the roots rather than adding to them is the point — the previous behaviour left
    /// every folder ever opened in the tree alongside Home, / and the volumes.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText = $"Folder not found: {path}";

            // A workspace that has been moved or unmounted should not keep being offered.
            if (RecentWorkspaces.Remove(path))
            {
                await PersistRecentAsync();
            }

            return;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        WorkspacePath = path;
        WorkspaceName = string.IsNullOrEmpty(name) ? path : name;

        FolderRoots.Clear();
        FolderRoots.Add(new FolderNodeViewModel(path, WorkspaceName, _folderBrowser));

        RecentWorkspaces.Remove(path);
        RecentWorkspaces.Insert(0, path);
        while (RecentWorkspaces.Count > AppSettings.MaxRecentWorkspaces)
        {
            RecentWorkspaces.RemoveAt(RecentWorkspaces.Count - 1);
        }

        await _settings.SaveAsync(_settings.Current.WithWorkspace(path));

        _workspaceIndexPending = true;
        await ScanFolderAsync(path);

        // After the scan, so the grid is populated before the tree walk for indexing begins.
        _workspaceIndexCts?.Cancel();
        _workspaceIndexCts?.Dispose();
        _workspaceIndexCts = new CancellationTokenSource();
        var token = _workspaceIndexCts.Token;
        _ = Task.Run(() => PrepareWorkspaceIndexAsync(path, token), token);
    }

    /// <summary>Returns to the no-workspace state, leaving the catalog and cache untouched.</summary>
    [RelayCommand]
    private async Task CloseWorkspaceAsync()
    {
        await CancelActiveScanAsync();

        _workspaceIndexCts?.Cancel();
        _indexCts?.Cancel();
        ShowIndexPrompt = false;
        CanIndexWorkspace = false;
        _workspaceFiles = [];

        WorkspacePath = null;
        WorkspaceName = null;
        FolderRoots.Clear();
        MediaItems.Clear();
        SelectedItem = null;
        SelectedFolder = null;
        CurrentFolderPath = null;
        SearchText = null;
        IsShowingSearchResults = false;

        await _settings.SaveAsync(_settings.Current with { LastWorkspacePath = null });
    }

    private Task PersistRecentAsync()
        => _settings.SaveAsync(_settings.Current with { RecentWorkspaces = RecentWorkspaces.ToList() });

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task ScanFolderAsync(string path)
    {
        await CancelActiveScanAsync();

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        CurrentFolderPath = path;
        MediaItems.Clear();
        SelectedItem = null;
        IsScanning = true;

        var stopwatch = Stopwatch.StartNew();
        var batch = new List<MediaItemViewModel>(BatchSize);
        var lastFlush = stopwatch.Elapsed;
        var count = 0;

        try
        {
            var options = new ScanOptions { Recursive = Recursive };

            await foreach (var file in _scanner.ScanAsync(path, options, cancellationToken: cts.Token))
            {
                // Re-scanning a folder must not lose the "modified" markers for edits already made.
                batch.Add(new MediaItemViewModel(file, _thumbnails)
                {
                    HasPendingChanges = _pending.HasChanges(file.FullPath)
                });
                count++;

                if (batch.Count >= BatchSize || stopwatch.Elapsed - lastFlush >= BatchInterval)
                {
                    Flush(batch);
                    lastFlush = stopwatch.Elapsed;
                    StatusText = $"Scanning {path} — {count} files";
                }
            }

            Flush(batch);
            StatusText = $"{count} media files in {path} ({stopwatch.Elapsed.TotalSeconds:0.0}s)";

            // Marks first, since most folders are already indexed and this is what puts the ratings
            // and labels on the tiles. Indexing calls it again afterwards if it changed anything.
            await LoadMarksAsync();

            // Indexing happens after the grid is populated so browsing is never blocked by it.
            _ = IndexScannedAsync(MediaItems.ToList());
        }
        catch (OperationCanceledException)
        {
            Flush(batch);
            StatusText = $"Scan cancelled — {count} files found";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan of {Folder} failed", path);
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                IsScanning = false;
                _scanCts = null;
            }

            cts.Dispose();
        }

        void Flush(List<MediaItemViewModel> pending)
        {
            foreach (var item in pending)
            {
                MediaItems.Add(item);
            }

            pending.Clear();
        }
    }

    private async Task CancelActiveScanAsync()
    {
        if (_scanCts is not { } active)
        {
            return;
        }

        await active.CancelAsync();
        _scanCts = null;
    }

    private async Task LoadPreviewAsync(MediaItemViewModel? item)
    {
        if (_previewCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        Preview = null;

        if (item is null)
        {
            _previewCts = null;
            IsPreviewLoading = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        IsPreviewLoading = true;

        try
        {
            // Interactive: the user selected this file and is waiting, so it must not queue behind
            // the grid's background tile work.
            var bytes = await _thumbnails.GetThumbnailAsync(
                item.File, PreviewEdgePixels, ThumbnailPriority.Interactive, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                Preview = new Bitmap(stream);
            }

            // After the pane has something in it, never before: the cheap preview is what makes
            // browsing feel immediate, and the full decode is only ever an upgrade to it.
            PrepareLoupeSource();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load preview for {File}", item.File.FullPath);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                IsPreviewLoading = false;
                _previewCts = null;
                cts.Dispose();
            }
        }
    }
}
