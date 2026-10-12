using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// A metadata field and whether the panel shows it. Saves as it is toggled — there is nothing here
/// worth an Apply button, and a settings dialog that could be closed with unsaved work is the only
/// way to lose the change.
/// </summary>
public sealed partial class MetadataFieldToggle : ObservableObject
{
    private readonly Action<MetadataField, bool> _changed;

    public MetadataFieldToggle(MetadataField field, string label, bool isVisible, Action<MetadataField, bool> changed)
    {
        Field = field;
        Label = label;
        _isVisible = isVisible;
        _changed = changed;
    }

    public MetadataField Field { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _changed(Field, value);
}

/// <summary>
/// A theme as the dropdown offers it. The description is worth carrying: the two themes differ by a
/// single step of grey, which a name alone does not convey to someone choosing between them.
/// </summary>
public sealed record ThemeChoice(AppTheme Theme, string Name, string Description);

/// <summary>Where the selection highlight takes its colour from, as the dropdown offers it.</summary>
public sealed record SelectionChoice(SelectionColour Source, string Name, string Description);

/// <summary>A typeface as the dropdown offers it.</summary>
public sealed record FontChoice(UiFont Font, string Name, string Description);

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// Limit tiers in megabytes. The small end matters as much as the large: a tight cap is the
    /// quickest way to see the rolling behaviour actually working.
    /// </summary>
    private static readonly int[] LimitChoicesMb = [50, 100, 250, 500, 1024, 2048, 5120, 10240, 20480, 51200];

    private readonly ISettingsService _settings;
    private readonly ICacheMaintenance _maintenance;
    private readonly IRenderCacheMaintenance _renderMaintenance;
    private readonly ICatalog _catalog;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settings,
        ICacheMaintenance maintenance,
        IRenderCacheMaintenance renderMaintenance,
        ICatalog catalog,
        KeywordLibraryEditorViewModel keywords,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _maintenance = maintenance;
        _renderMaintenance = renderMaintenance;
        _catalog = catalog;
        _paths = paths;
        _logger = logger;
        Keywords = keywords;

        var current = settings.Current;

        // Matched by value, and falling back to the first entry so a theme this build no longer
        // offers leaves the dropdown showing something rather than blank.
        _selectedTheme = Themes.FirstOrDefault(choice => choice.Theme == current.Theme) ?? Themes[0];
        _selectedSelectionColour =
            SelectionColours.FirstOrDefault(choice => choice.Source == current.SelectionColour)
            ?? SelectionColours[0];

        _selectedFont = Fonts.FirstOrDefault(choice => choice.Font == current.UiFont) ?? Fonts[0];
        _handDrawnSelection = current.SelectionStyle == SelectionStyle.HandDrawn;
        _handDrawnRoughness = current.ClampedRoughness;
        _handDrawnAnimates = current.HandDrawnAnimates;
        _contactSheet = current.ContactSheet;

        _isCacheLimited = current.IsCacheLimited;
        _selectedLimitIndex = current.IsCacheLimited
            ? NearestChoice(current.CacheSizeLimitBytes)
            : DefaultChoiceIndex;

        _interfaceBrightness = current.ClampedBrightness;
        _restrictKeywordsToLibrary = current.RestrictKeywordsToLibrary;
        BuildFieldToggles();
        BuildLabelRows();
        _renderCacheEnabled = current.RenderCacheEnabled;
        _selectedRenderLimitIndex = current.IsRenderCacheLimited
            ? NearestChoice(current.RenderCacheSizeLimitBytes)
            : DefaultRenderChoiceIndex;

        CachePath = paths.CacheRoot;
        IsUsingDefaultCachePath = string.IsNullOrWhiteSpace(current.CacheDirectoryOverride);
        CatalogPath = paths.CatalogPath;
        IsUsingDefaultCatalogPath = string.IsNullOrWhiteSpace(current.CatalogDirectoryOverride);
    }

    /// <summary>Supplied by the view; the ViewModel does not reach for the window itself.</summary>
    public IStorageProvider? StorageProvider { get; set; }

    /// <summary>The Keywords tab. Its own ViewModel — it shares nothing with cache housekeeping.</summary>
    public KeywordLibraryEditorViewModel Keywords { get; }

    /// <summary>
    /// The open workspace, so importing keywords can be scoped to it. Passed through rather than
    /// looked up: the settings dialog has no business knowing about the main window.
    /// </summary>
    public string? WorkspacePath
    {
        get => Keywords.WorkspacePath;
        set => Keywords.WorkspacePath = value;
    }

    public static IReadOnlyList<string> LimitChoices { get; } =
        LimitChoicesMb.Select(mb => mb < 1024 ? $"{mb} MB" : $"{mb / 1024} GB").ToList();

    [ObservableProperty]
    private string _cachePath = string.Empty;

    [ObservableProperty]
    private bool _isUsingDefaultCachePath;

    [ObservableProperty]
    private string _cacheSizeDisplay = "Calculating…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isCacheLimited;

    [ObservableProperty]
    private int _selectedLimitIndex;

    /// <summary>Two-step confirmation, so a mis-click cannot throw away a large cache.</summary>
    [ObservableProperty]
    private bool _isConfirmingClear;

    [ObservableProperty]
    private string _catalogPath = string.Empty;

    [ObservableProperty]
    private bool _isUsingDefaultCatalogPath;

    [ObservableProperty]
    private string _catalogSizeDisplay = "Calculating…";

    [ObservableProperty]
    private bool _isConfirmingCatalogClear;

    public string LimitSummary => IsCacheLimited
        ? $"Oldest thumbnails are removed once the cache passes {ByteSize.Format(SelectedLimitBytes)}."
        : "The cache grows without limit.";

    private long SelectedLimitBytes
        => LimitChoicesMb[Math.Clamp(SelectedLimitIndex, 0, LimitChoicesMb.Length - 1)] * 1024L * 1024L;

    /// <summary>2 GB — a sensible default ceiling for a thumbnail cache.</summary>
    private static readonly int DefaultChoiceIndex = Array.IndexOf(LimitChoicesMb, 2048);

    private static int NearestChoice(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        var best = 0;
        for (var i = 1; i < LimitChoicesMb.Length; i++)
        {
            if (Math.Abs(LimitChoicesMb[i] - mb) < Math.Abs(LimitChoicesMb[best] - mb))
            {
                best = i;
            }
        }

        return best;
    }

    partial void OnIsCacheLimitedChanged(bool value)
    {
        OnPropertyChanged(nameof(LimitSummary));
        _ = ApplyLimitAsync();
    }

    partial void OnSelectedLimitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LimitSummary));
        _ = ApplyLimitAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var stats = await _maintenance.GetStatisticsAsync().ConfigureAwait(true);
            CacheSizeDisplay = stats.FileCount == 0
                ? "Empty"
                : $"{ByteSize.Format(stats.TotalBytes)} in {stats.FileCount:N0} files";
            CachePath = _paths.CacheRoot;

            var renders = await _renderMaintenance.GetStatisticsAsync().ConfigureAwait(true);
            RenderCacheSizeDisplay = renders.FileCount == 0
                ? "Empty"
                : $"{ByteSize.Format(renders.TotalBytes)} in {renders.FileCount:N0} renditions";

            var catalog = await _catalog.GetStatisticsAsync().ConfigureAwait(true);
            CatalogSizeDisplay = catalog.FileCount == 0
                ? "Empty"
                : $"{catalog.FileCount:N0} files, {catalog.KeywordCount:N0} keywords · {ByteSize.Format(catalog.SizeBytes)}";
            CatalogPath = _paths.CatalogPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not measure the cache");
            CacheSizeDisplay = "Unavailable";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginClearCache() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClearCache() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ConfirmClearCacheAsync()
    {
        IsConfirmingClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var freed = await _maintenance.ClearAsync().ConfigureAwait(true);
            StatusMessage = $"Cleared {ByteSize.Format(freed)}. Thumbnails regenerate as you browse.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the cache");
            StatusMessage = $"Could not clear the cache: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ChooseCacheFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a cache location",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await SaveAsync(_settings.Current with { CacheDirectoryOverride = path }).ConfigureAwait(true);
        StatusMessage = "Cache location changed. Existing thumbnails stay at the old location.";
        IsUsingDefaultCachePath = false;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UseDefaultCacheFolderAsync()
    {
        await SaveAsync(_settings.Current with { CacheDirectoryOverride = null }).ConfigureAwait(true);
        IsUsingDefaultCachePath = true;
        StatusMessage = "Using the default cache location.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BeginClearCatalog() => IsConfirmingCatalogClear = true;

    [RelayCommand]
    private void CancelClearCatalog() => IsConfirmingCatalogClear = false;

    [RelayCommand]
    private async Task ConfirmClearCatalogAsync()
    {
        IsConfirmingCatalogClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            await _catalog.ClearAsync().ConfigureAwait(true);
            StatusMessage = "Catalog cleared. Folders are re-indexed as you browse them.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the catalog");
            StatusMessage = $"Could not clear the catalog: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Drops entries whose files are gone. Cheaper and far less disruptive than clearing when the
    /// catalog has simply accumulated references to files that were moved or deleted.
    /// </summary>
    [RelayCommand]
    private async Task PruneCatalogAsync()
    {
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var removed = await _catalog.RemoveMissingAsync().ConfigureAwait(true);
            StatusMessage = removed == 0
                ? "Every catalog entry still points at a file that exists."
                : $"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} for files that no longer exist.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune the catalog");
            StatusMessage = $"Could not prune the catalog: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ChooseCatalogFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a catalog location",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await SaveAsync(_settings.Current with { CatalogDirectoryOverride = path }).ConfigureAwait(true);
        IsUsingDefaultCatalogPath = false;
        StatusMessage = "Catalog location changed. A new, empty catalog is created there — the old one stays where it was.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UseDefaultCatalogFolderAsync()
    {
        await SaveAsync(_settings.Current with { CatalogDirectoryOverride = null }).ConfigureAwait(true);
        IsUsingDefaultCatalogPath = true;
        StatusMessage = "Using the default catalog location.";
        await RefreshAsync().ConfigureAwait(true);
    }

    // ---- Display ------------------------------------------------------------------------------

    /// <summary>
    /// One toggle per metadata field. Built once and bound two ways; each toggle saves itself, so
    /// there is no Apply to forget.
    /// </summary>
    public IReadOnlyList<MetadataFieldToggle> FieldToggles { get; private set; } = [];

    private void BuildFieldToggles()
    {
        var hidden = _settings.Current.HiddenMetadataFields.ToHashSet();

        FieldToggles =
        [
            .. Enum.GetValues<MetadataField>()
                .Select(field => new MetadataFieldToggle(field, Describe(field), !hidden.Contains(field), SetFieldVisible))
        ];
    }

    private static string Describe(MetadataField field) => field switch
    {
        MetadataField.Rating => "Rating",
        MetadataField.Title => "Title",
        MetadataField.Headline => "Headline",
        MetadataField.Description => "Description",
        MetadataField.Keywords => "Keywords",
        MetadataField.Label => "Label",
        MetadataField.Creator => "Creator",
        MetadataField.Copyright => "Copyright",
        _ => field.ToString()
    };

    private void SetFieldVisible(MetadataField field, bool visible)
    {
        var hidden = _settings.Current.HiddenMetadataFields.ToList();

        if (visible)
        {
            hidden.RemoveAll(f => f == field);
        }
        else if (!hidden.Contains(field))
        {
            hidden.Add(field);
        }

        _ = SaveAsync(_settings.Current with { HiddenMetadataFields = hidden });
    }

    // ---- General ------------------------------------------------------------------------------

    public static IReadOnlyList<ThemeChoice> Themes { get; } =
    [
        new(AppTheme.Darkroom, "Darkroom", "Near-black, so nothing competes with the photograph."),
        new(AppTheme.Graphite, "Graphite", "One dark grey throughout, with no panel lighter than another."),
        new(AppTheme.Safelight, "Safelight", "Deep red, after the lamp a darkroom is lit by."),
        new(AppTheme.Verdigris, "Verdigris", "Dark teal, after the green that grows on weathered copper.")
    ];

    /// <summary>
    /// Applied the moment it is picked, and to windows that are already open — the point of choosing
    /// between two shades of dark is seeing them, which a preview swatch or a restart both defeat.
    /// </summary>
    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeChoice value)
    {
        if (_settings.Current.Theme != value.Theme)
        {
            _ = SaveAsync(_settings.Current with { Theme = value.Theme });
        }
    }

    public static IReadOnlyList<SelectionChoice> SelectionColours { get; } =
    [
        new(SelectionColour.System, "System default",
            "The colour your operating system highlights with, and it follows if you change it."),
        new(SelectionColour.Theme, "Match the theme",
            "A quieter highlight drawn from the theme's own colours.")
    ];

    [ObservableProperty]
    private SelectionChoice _selectedSelectionColour;

    partial void OnSelectedSelectionColourChanged(SelectionChoice value)
    {
        if (_settings.Current.SelectionColour != value.Source)
        {
            _ = SaveAsync(_settings.Current with { SelectionColour = value.Source });
        }
    }

    // ---- Experimental -------------------------------------------------------------------------

    /// <summary>
    /// Whether the selected folder is ringed by hand rather than filled. Experimental, and off
    /// unless asked for — the look is a matter of taste, and it grows eccentric around a long
    /// folder name in a way that some will like and some will not.
    /// </summary>
    [ObservableProperty]
    private bool _handDrawnSelection;

    partial void OnHandDrawnSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(HandDrawnSummary));

        var style = value ? SelectionStyle.HandDrawn : SelectionStyle.Standard;

        if (_settings.Current.SelectionStyle != style)
        {
            _ = SaveAsync(_settings.Current with { SelectionStyle = style });
        }
    }

    /// <summary>
    /// How far the ring wanders off a true ellipse. Offered as a slider because the pleasing value
    /// is a matter of taste and cannot be argued to from first principles.
    /// </summary>
    [ObservableProperty]
    private double _handDrawnRoughness;

    partial void OnHandDrawnRoughnessChanged(double value)
    {
        OnPropertyChanged(nameof(HandDrawnSummary));

        if (Math.Abs(_settings.Current.HandDrawnRoughness - value) > 0.001)
        {
            _ = SaveAsync(_settings.Current with { HandDrawnRoughness = value });
        }
    }

    /// <summary>
    /// How brightly the interface is drawn. Applied as it is dragged, like the theme — the point of
    /// the control is to find the level that suits the room you are in, and that cannot be judged
    /// from a number.
    /// </summary>
    [ObservableProperty]
    private double _interfaceBrightness = 1.0;

    partial void OnInterfaceBrightnessChanged(double value)
    {
        OnPropertyChanged(nameof(BrightnessSummary));

        if (Math.Abs(_settings.Current.InterfaceBrightness - value) > 0.001)
        {
            _ = SaveAsync(_settings.Current with { InterfaceBrightness = value });
        }
    }

    public string BrightnessSummary => InterfaceBrightness >= AppSettings.MaxBrightness - 0.001
        ? "Full brightness — the theme as designed."
        : $"Dimmed to {InterfaceBrightness:P0}. Photographs are never dimmed; only the application around them.";

    public static double MinBrightness => AppSettings.MinBrightness;

    public static double MaxBrightness => AppSettings.MaxBrightness;

    [ObservableProperty]
    private bool _handDrawnAnimates;

    partial void OnHandDrawnAnimatesChanged(bool value)
    {
        if (_settings.Current.HandDrawnAnimates != value)
        {
            _ = SaveAsync(_settings.Current with { HandDrawnAnimates = value });
        }
    }

    /// <summary>
    /// The contact sheet: grease-pencil verdicts, a rebate over each frame, film edges between the
    /// panes. Its own switch rather than a second mode of the hand-drawn selection, because the
    /// two are separately wanted — and it takes effect as it is ticked, like everything here.
    /// </summary>
    [ObservableProperty]
    private bool _contactSheet;

    partial void OnContactSheetChanged(bool value)
    {
        if (_settings.Current.ContactSheet != value)
        {
            _ = SaveAsync(_settings.Current with { ContactSheet = value });
        }
    }

    // ---- Labels --------------------------------------------------------------------------------

    /// <summary>
    /// The colour labels, editable. Saved as they are typed, like everything else here.
    /// </summary>
    public ObservableCollection<LabelRowViewModel> LabelRows { get; } = [];

    private bool _loadingLabels;

    private void BuildLabelRows()
    {
        _loadingLabels = true;

        try
        {
            LabelRows.Clear();

            foreach (var label in _settings.Current.Labels.Labels)
            {
                LabelRows.Add(new LabelRowViewModel(label.Name, label.Colour, SaveLabels));
            }
        }
        finally
        {
            _loadingLabels = false;
        }
    }

    private void SaveLabels()
    {
        if (_loadingLabels)
        {
            return;
        }

        // Blank names are dropped rather than saved: an empty label would be written to files as an
        // empty string, which is indistinguishable from having no label at all.
        var labels = LabelRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => new LabelDefinition(row.Name.Trim(), row.Colour))
            .ToImmutableArray();

        _ = SaveAsync(_settings.Current with { Labels = new LabelLibrary { Labels = labels } });
    }

    [ObservableProperty]
    private bool _isImportingLabels;

    /// <summary>
    /// Removes a label from the library.
    ///
    /// Nothing is written to any photograph: the file stores the word, so a file labelled "Yellow"
    /// still is, and the inspector still shows it — as a label the library does not define, which is
    /// how a label set in another application has always been handled. What goes is the swatch to
    /// filter by and the entry to apply.
    ///
    /// <para>Labels below it move up a slot, and the slot is the number digiKam and Photo Mechanic
    /// write. That is unavoidable in a delete and is why the tooltip says so.</para>
    /// </summary>
    [RelayCommand]
    private void RemoveLabel(LabelRowViewModel? row)
    {
        if (row is not null && LabelRows.Remove(row))
        {
            SaveLabels();
        }
    }

    /// <summary>
    /// Adds an empty label to fill in.
    ///
    /// Here because deleting without it is a one-way door: a label removed by mistake could
    /// otherwise only come back by importing one off a photograph that still carries it, and if
    /// nothing does, not at all.
    /// </summary>
    [RelayCommand]
    private void AddLabel()
    {
        LabelRows.Add(new LabelRowViewModel(string.Empty, LabelColours.Unrecognised, SaveLabels));

        // Not saved yet: a blank name is dropped on save, so the row would vanish as it appeared.
        // It is written as soon as it is given a name.
    }

    /// <summary>
    /// Adds the labels the photographs already carry to the library.
    ///
    /// The same idea as importing keywords, and the more useful of the two for compatibility: the
    /// file stores a word, not a colour, so a workspace labelled in Lightroom arrives carrying
    /// "Yellow" and "Green" while the library still holds Bridge's "Select" and "Second". Those
    /// labels are shown on tiles and found by the search, but until they are in the library there is
    /// no swatch to filter by and no way to apply one to another photograph.
    ///
    /// <para><b>Appended, never reordered.</b> A label's position is its slot, and the slot is what
    /// digiKam and Photo Mechanic write as a number. Inserting an imported label above an existing
    /// one would silently change what those numbers mean for every file already labelled.</para>
    /// </summary>
    [RelayCommand]
    private async Task ImportLabelsAsync()
    {
        IsImportingLabels = true;
        StatusMessage = null;

        try
        {
            var found = await _catalog.GetLabelsAsync(WorkspacePath).ConfigureAwait(true);
            if (found.Count == 0)
            {
                StatusMessage = WorkspacePath is null
                    ? "No labels found in the catalog."
                    : "No labels found in this workspace. Index it first, or open a workspace that has some.";
                return;
            }

            var library = _settings.Current.Labels;
            var plan = LabelImport.Plan(library, found.Select(label => label.Value));

            if (Keywords.ConfirmImport is { } confirm && !await confirm(plan, "label").ConfigureAwait(true))
            {
                StatusMessage = plan.HasAnythingToAdd
                    ? "Import cancelled. Nothing was changed."
                    : $"Found {plan.Considered:N0} label(s); all of them were already in the library.";
                return;
            }

            if (!plan.HasAnythingToAdd)
            {
                StatusMessage = $"Found {plan.Considered:N0} label(s); all of them were already in the library.";
                return;
            }

            await SaveAsync(_settings.Current with { Labels = LabelImport.Apply(library, plan) })
                .ConfigureAwait(true);

            BuildLabelRows();

            StatusMessage = $"Imported {plan.ToAdd.Length:N0} new label(s) from {plan.Considered:N0} found.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not import labels");
            StatusMessage = $"Could not import labels: {ex.Message}";
        }
        finally
        {
            IsImportingLabels = false;
        }
    }

    public static double MinRoughness => AppSettings.MinRoughness;

    public static double MaxRoughness => AppSettings.MaxRoughness;

    public static IReadOnlyList<FontChoice> Fonts { get; } =
    [
        new(UiFont.System, "System default", "Whatever this computer normally uses."),
        new(UiFont.Andika, "Andika",
            "Friendly but plainly legible — drawn for teaching reading, so it stays clear on filenames and numbers."),
        new(UiFont.Delius, "Delius",
            "Properly handwritten and still even. Warmer than Andika, and a little less clear on long numbers.")
    ];

    /// <summary>
    /// The interface typeface. Separate from the hand-drawn marks: some will want one without the
    /// other, and the font is by far the more far-reaching of the two.
    /// </summary>
    [ObservableProperty]
    private FontChoice _selectedFont;

    partial void OnSelectedFontChanged(FontChoice value)
    {
        if (_settings.Current.UiFont != value.Font)
        {
            _ = SaveAsync(_settings.Current with { UiFont = value.Font });
        }
    }

    public string HandDrawnSummary => HandDrawnSelection
        ? $"Roughness {HandDrawnRoughness:0.00} — below about 0.4 it reads as a plain oval, above about 1.6 it starts to look scribbled."
        : "The selected folder is filled, as everywhere else.";

    // ---- Keywords -----------------------------------------------------------------------------

    /// <summary>
    /// Whether keywords may only come from the library. Applied immediately — the metadata panel
    /// reads the setting rather than being told about it.
    /// </summary>
    [ObservableProperty]
    private bool _restrictKeywordsToLibrary;

    partial void OnRestrictKeywordsToLibraryChanged(bool value)
    {
        if (_settings.Current.RestrictKeywordsToLibrary != value)
        {
            _ = SaveAsync(_settings.Current with { RestrictKeywordsToLibrary = value });
        }
    }

    // ---- Render cache -------------------------------------------------------------------------

    /// <summary>
    /// Whether developed RAW files are kept on disk. Off by choice for anyone short of space: a
    /// rendition is tens of megabytes against a thumbnail's tens of kilobytes.
    /// </summary>
    [ObservableProperty]
    private bool _renderCacheEnabled;

    [ObservableProperty]
    private int _selectedRenderLimitIndex;

    [ObservableProperty]
    private string _renderCacheSizeDisplay = "—";

    [ObservableProperty]
    private bool _isConfirmingRenderClear;

    /// <summary>10 GB — roughly fifteen hundred 26MP renditions, a working set rather than a library.</summary>
    private static readonly int DefaultRenderChoiceIndex = Array.IndexOf(LimitChoicesMb, 10240);

    private long SelectedRenderLimitBytes
        => LimitChoicesMb[Math.Clamp(SelectedRenderLimitIndex, 0, LimitChoicesMb.Length - 1)] * 1024L * 1024L;

    public string RenderLimitSummary => RenderCacheEnabled
        ? $"Least recently opened renditions are removed once they pass {ByteSize.Format(SelectedRenderLimitBytes)}."
        : "RAW files are developed afresh every time they are opened.";

    partial void OnRenderCacheEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(RenderLimitSummary));
        _ = ApplyRenderCacheAsync(clearWhenDisabled: true);
    }

    partial void OnSelectedRenderLimitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RenderLimitSummary));
        _ = ApplyRenderCacheAsync(clearWhenDisabled: false);
    }

    private async Task ApplyRenderCacheAsync(bool clearWhenDisabled)
    {
        var limit = SelectedRenderLimitBytes;

        if (_settings.Current.RenderCacheEnabled == RenderCacheEnabled &&
            _settings.Current.RenderCacheSizeLimitBytes == limit)
        {
            return;
        }

        await SaveAsync(_settings.Current with
        {
            RenderCacheEnabled = RenderCacheEnabled,
            RenderCacheSizeLimitBytes = limit
        }).ConfigureAwait(true);

        // Turning it off is a request to stop spending disk, so the space comes back now rather than
        // sitting there unused. Trimming handles both cases: it empties a disabled cache.
        if (clearWhenDisabled || RenderCacheEnabled)
        {
            var freed = await _renderMaintenance.TrimAsync().ConfigureAwait(true);
            if (freed > 0)
            {
                StatusMessage = $"Released {ByteSize.Format(freed)} of developed RAW files.";
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BeginClearRenderCache() => IsConfirmingRenderClear = true;

    [RelayCommand]
    private void CancelClearRenderCache() => IsConfirmingRenderClear = false;

    [RelayCommand]
    private async Task ConfirmClearRenderCacheAsync()
    {
        IsConfirmingRenderClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var freed = await _renderMaintenance.ClearAsync().ConfigureAwait(true);
            StatusMessage = $"Released {ByteSize.Format(freed)}. RAW files develop again on first view.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the render cache");
            StatusMessage = $"Could not clear the render cache: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task ApplyLimitAsync()
    {
        var limit = IsCacheLimited ? SelectedLimitBytes : AppSettings.UnlimitedCache;
        if (_settings.Current.CacheSizeLimitBytes == limit)
        {
            return;
        }

        await SaveAsync(_settings.Current with { CacheSizeLimitBytes = limit }).ConfigureAwait(true);

        if (!IsCacheLimited)
        {
            return;
        }

        // Applying a limit should take effect now, not at some later write.
        var freed = await _maintenance.TrimAsync().ConfigureAwait(true);
        if (freed > 0)
        {
            StatusMessage = $"Trimmed {ByteSize.Format(freed)} to fit the new limit.";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync(AppSettings settings)
    {
        try
        {
            await _settings.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            StatusMessage = $"Could not save settings: {ex.Message}";
        }
    }
}
