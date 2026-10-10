using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.UI.Controls;
using BetterDAM.UI.Services;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class MainWindow : Window
{
    /// <summary>Creates the settings ViewModel on demand, so its state is fresh each time.</summary>
    public Func<SettingsViewModel>? SettingsViewModelFactory { get; set; }

    /// <summary>Likewise for sync: each dialog re-plans against the current pending changes.</summary>
    public Func<SyncViewModel>? SyncViewModelFactory { get; set; }

    /// <summary>And for Prepare Workspace, which re-counts the workspace each time it is opened.</summary>
    public Func<PrepareWorkspaceViewModel>? PrepareWorkspaceViewModelFactory { get; set; }

    /// <summary>And for the Changes review, which reads the pending store afresh each time.</summary>
    public Func<ChangesViewModel>? ChangesViewModelFactory { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        // Assigned here rather than in the markup: x:Name inside a property element generates no
        // field, so a named transform there cannot be reached from code.
        PreviewLoupe.RenderTransform = _loupePosition;

        // Tunnelled: the thumbnail grid is a ListBox, and a ListBox treats Space as "toggle the
        // focused item". Waiting for the key to bubble up would mean never seeing it while the grid
        // has focus, which is exactly where it is during playback.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        // Coming back to the window is when a stale view starts to matter, and it is the moment
        // Unity's editor uses to re-import assets changed underneath it. The check is cheap enough
        // to hang off an event that fires whenever the window is clicked on.
        Activated += OnWindowActivated;

        // Tunnelled too: while the field list is open, Down, Enter and Escape belong to it rather
        // than to the text box or to the search command.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.LostFocus += (_, _) => (DataContext as MainWindowViewModel)?.DismissFieldSuggestions();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            viewModel.StorageProvider = StorageProvider;

            viewModel.RecentWorkspaces.CollectionChanged -= OnRecentChanged;
            viewModel.RecentWorkspaces.CollectionChanged += OnRecentChanged;
            RebuildRecentMenu();

            // The Workspace menu appears and disappears with the workspace itself.
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateWorkspaceMenu();

            viewModel.ScrollSelectionIntoView -= OnScrollSelectionIntoView;
            viewModel.ScrollSelectionIntoView += OnScrollSelectionIntoView;

            viewModel.Inspector.CompareConflictsRequested -= OnCompareConflicts;
            viewModel.Inspector.CompareConflictsRequested += OnCompareConflicts;
            ApplyFilmstripLayout(viewModel.Filmstrip);

            // Frames are pushed rather than bound: a binding would mean allocating a bitmap per
            // frame, where the surface reuses one and blits into it.
            viewModel.Player.FrameReady -= OnFrameReady;
            viewModel.Player.FrameReady += OnFrameReady;
            viewModel.Player.SurfaceCleared -= OnSurfaceCleared;
            viewModel.Player.SurfaceCleared += OnSurfaceCleared;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.WorkspacePath) or nameof(MainWindowViewModel.HasWorkspace))
        {
            UpdateWorkspaceMenu();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.Filmstrip)
                 && DataContext is MainWindowViewModel viewModel)
        {
            ApplyFilmstripLayout(viewModel.Filmstrip);
        }
    }

    /// <summary>
    /// After a layout change the selected tile is usually off screen, and it is the one the user was
    /// looking at. Deferred so the new row arrangement has been measured before anything is scrolled.
    /// </summary>
    private void OnScrollSelectionIntoView(MediaItemViewModel item)
        => Dispatcher.UIThread.Post(() => ThumbnailGrid.ScrollIntoView(item), DispatcherPriority.Background);

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UpdateSelection(list.SelectedItems?.OfType<MediaItemViewModel>().ToList() ?? []);
        }
    }

    private void OnFrameReady(VideoFrame frame) => VideoSurface.Present(frame);

    private void OnSurfaceCleared() => VideoSurface.Clear();

    private async void OnOpenSync(object? sender, RoutedEventArgs e)
    {
        if (SyncViewModelFactory is not { } factory)
        {
            return;
        }

        var window = new SyncWindow { DataContext = factory() };
        await window.ShowDialog(this);

        // Sync clears whatever it committed, so the grid's badges need refreshing.
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RefreshAfterSync();
        }
    }

    private void OnOpenChanges(object? sender, RoutedEventArgs e) => _ = ReviewChangesAsync(quitting: false);

    /// <summary>
    /// The inspector's conflict warning asking for the comparison. Reachable whether or not anything
    /// is pending — until now the review could only be opened from the pending count, so a file that
    /// conflicted but had no edits had no way in at all.
    /// </summary>
    private void OnCompareConflicts(MediaFile file) => _ = ReviewChangesAsync(quitting: false, file);

    /// <summary>
    /// Opens the review of pending changes and, when it closes, brings the grid up to date with
    /// whatever was written or discarded there.
    ///
    /// Returns true only when the review was opened because the window was closing and the user
    /// chose to quit regardless — the one answer <see cref="OnClosing"/> needs.
    /// </summary>
    private async Task<bool> ReviewChangesAsync(bool quitting, MediaFile? focusConflict = null)
    {
        if (ChangesViewModelFactory is not { } factory || DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        var review = factory();
        review.WorkspacePath = viewModel.WorkspacePath;
        review.Scope = viewModel.MediaItems.Select(item => item.File).ToList();
        review.IsQuitPrompt = quitting;
        review.FocusConflict = focusConflict;

        // Started rather than awaited: it reads one file, and the window should be up while that
        // happens rather than after it. It reports its own failures into the view model.
        _ = review.InitialiseAsync();

        var quitAnyway = await new ChangesWindow { DataContext = review }.ShowDialog<bool>(this);

        // Nothing to bring up to date on the way out: the refresh re-reads the inspected file, and
        // doing that while the application is tearing down only logs a read against a disposed
        // ExifTool session.
        if (!quitAnyway)
        {
            await viewModel.RefreshAfterReviewAsync(review.Written);
        }

        return quitAnyway;
    }

    /// <summary>Set once the user has seen what would be lost and chosen to lose it.</summary>
    private bool _quitConfirmed;

    /// <summary>
    /// The pending store is held in memory. Closing with edits in it would lose them silently, so
    /// the close is held and the review opened instead — a yes/no here would be a question the
    /// user cannot answer without seeing which files it is about. Quitting from the review closes
    /// the window for real; closing the review returns to the application.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_quitConfirmed || e.Cancel || DataContext is not MainWindowViewModel { HasPendingChanges: true })
        {
            return;
        }

        e.Cancel = true;

        Dispatcher.UIThread.Post(async () =>
        {
            if (await ReviewChangesAsync(quitting: true))
            {
                _quitConfirmed = true;
                Close();
            }
        });
    }

    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRecentMenu();

    /// <summary>
    /// Walks the declared menu for the Open Recent placeholder. Necessary because x:Name generates
    /// no field for a NativeMenuItem — it is not part of the visual tree.
    /// </summary>
    private NativeMenu? FindRecentMenu()
        => NativeMenu.GetMenu(this)?
            .Items.OfType<NativeMenuItem>()
            .SelectMany(top => top.Menu?.Items.OfType<NativeMenuItem>() ?? [])
            .FirstOrDefault(item => item.Header == MenuConventions.OpenRecentHeader)?
            .Menu;

    /// <summary>
    /// Rebuilds Open Recent from the ViewModel. Built in code rather than bound because
    /// <see cref="NativeMenuItem"/> has no DataContext, so an ItemsSource-style binding has nothing
    /// to resolve against.
    /// </summary>
    private void RebuildRecentMenu()
    {
        if (FindRecentMenu() is not { } menu || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        menu.Items.Clear();

        foreach (var path in viewModel.RecentWorkspaces)
        {
            var item = new NativeMenuItem { Header = WorkspaceLabel.ForMenu(path), ToolTip = path };

            var target = path;
            item.Click += (_, _) => _ = viewModel.OpenPathAsync(target);
            menu.Items.Add(item);
        }

        // An always-empty submenu looks broken, so say why it is empty.
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem { Header = "No recent workspaces", IsEnabled = false });
        }
    }

    /// <summary>
    /// Finds a top-level menu by header. Necessary for the same reason as
    /// <see cref="FindRecentMenu"/>: x:Name generates no field for a NativeMenuItem, because it is
    /// not part of the visual tree.
    /// </summary>
    private NativeMenuItem? FindTopLevelMenu(string header)
        => NativeMenu.GetMenu(this)?
            .Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header == header);

    /// <summary>
    /// Shows the Workspace menu only while a workspace is open. A menu of things that cannot be done
    /// is worse than no menu: it invites a click and then explains itself with a disabled item.
    /// </summary>
    private void UpdateWorkspaceMenu()
    {
        if (FindTopLevelMenu(MenuConventions.WorkspaceHeader) is { } menu &&
            DataContext is MainWindowViewModel viewModel)
        {
            menu.IsVisible = viewModel.HasWorkspace;
        }
    }

    private async void OnPrepareWorkspace(object? sender, EventArgs e)
    {
        if (PrepareWorkspaceViewModelFactory is not { } factory ||
            DataContext is not MainWindowViewModel { WorkspacePath: { } workspace })
        {
            return;
        }

        var viewModel = factory();
        viewModel.WorkspacePath = workspace;

        await new PrepareWorkspaceWindow { DataContext = viewModel }.ShowDialog(this);
    }

    private void OnCloseWorkspace(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.CloseWorkspaceCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Space plays and pauses the inline preview, the way it does in every other video player.
    ///
    /// Tunnelled, so it beats the thumbnail grid's own use of Space — but only when a video is
    /// selected, leaving the key to the grid the rest of the time, and never while typing.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space
            || e.KeyModifiers != KeyModifiers.None
            || FocusManager?.GetFocusedElement() is TextBox
            || DataContext is not MainWindowViewModel { IsVideoSelected: true } viewModel)
        {
            return;
        }

        viewModel.Player.TogglePlayCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Esc leaves Inspect. Checked before F so a pinned loupe is what Esc acts on.
        if (e.Key == Key.Escape && _loupePinned)
        {
            StopInspecting();
            e.Handled = true;
            return;
        }

        // Cmd/Ctrl+F means the library search, from anywhere in the window and whatever the
        // inspector is showing. The XMP tab's tag filter is a visible box rather than a second
        // owner of this shortcut: a Find key that lands somewhere different depending on which tab
        // happens to be open is worse than one that always lands in the same place.
        if (e.Key == Key.F && e.KeyModifiers == MenuConventions.Accelerator)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ignored while typing: "f" belongs to the search box when it has focus.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox)
        {
            OpenFullscreen();
            e.Handled = true;
            return;
        }

        // E for the filmstrip, which is the key Lightroom uses for the same idea of one picture
        // large with the shoot along the bottom. Ignored while typing, like F.
        if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.None
            && FocusManager?.GetFocusedElement() is not TextBox
            && DataContext is MainWindowViewModel filmstripTarget)
        {
            filmstripTarget.Filmstrip = !filmstripTarget.Filmstrip;
            e.Handled = true;
        }
    }

    // ---- Filmstrip ----------------------------------------------------------------------------

    /// <summary>
    /// How much of a row has to be left before a drag counts as asking for the filmstrip. Just over
    /// one row, so the snap happens while a second row is still being squeezed out rather than
    /// after the first has already been cut in half.
    /// </summary>
    private const double FilmstripSnapRows = 1.35;

    /// <summary>
    /// The thumbnail and preview heights from before the filmstrip, so leaving it puts the divider
    /// back where it was rather than at some default.
    /// </summary>
    private GridLength? _gridThumbnailHeight;
    private GridLength? _gridPreviewHeight;

    private RowDefinition ThumbnailRow() => ContentRows.RowDefinitions[1];

    private RowDefinition PreviewRow() => ContentRows.RowDefinitions[3];

    /// <summary>
    /// Moves the star from the thumbnails to the preview and back.
    ///
    /// Done here rather than by binding the row heights because the splitter writes to those same
    /// heights as it is dragged, and a binding would be fighting it for ownership.
    /// </summary>
    private void ApplyFilmstripLayout(bool filmstrip)
    {
        if (filmstrip)
        {
            if (_gridThumbnailHeight is null)
            {
                _gridThumbnailHeight = ThumbnailRow().Height;
                _gridPreviewHeight = PreviewRow().Height;
            }

            // Auto, so the row is exactly as tall as one tile and the thumbnail size still governs it.
            ThumbnailRow().Height = GridLength.Auto;
            PreviewRow().Height = new GridLength(1, GridUnitType.Star);
            ThumbnailGrid.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            ThumbnailGrid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        }
        else
        {
            ThumbnailRow().Height = _gridThumbnailHeight ?? new GridLength(1, GridUnitType.Star);
            PreviewRow().Height = _gridPreviewHeight ?? new GridLength(280);
            _gridThumbnailHeight = null;
            _gridPreviewHeight = null;
            ThumbnailGrid.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            ThumbnailGrid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        }
    }

    /// <summary>
    /// Dragging the divider to the floor is the other way into the filmstrip, and dragging it back
    /// up is the way out. The gesture already means "give me more picture", so the mode is found by
    /// doing the obvious thing rather than by locating a checkbox.
    /// </summary>
    private void OnPreviewSplitterDragged(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var tile = viewModel.ThumbnailSize + TileChrome;

        if (!viewModel.Filmstrip)
        {
            if (ThumbnailRow().ActualHeight < tile * FilmstripSnapRows)
            {
                viewModel.Filmstrip = true;
            }

            return;
        }

        // Leaving: the drag has to have pulled the row open past the point it would snap shut at,
        // otherwise letting go anywhere near the bottom would flip the mode back and forth.
        if (ThumbnailRow().ActualHeight > tile * FilmstripSnapRows)
        {
            viewModel.Filmstrip = false;
        }
    }

    /// <summary>
    /// Roughly what a tile adds to its thumbnail: the caption, the margins and the rebate. Only used
    /// to decide when a drag has run out of room for a second row, so an approximation is enough.
    /// </summary>
    private const double TileChrome = 40;

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) => OpenFullscreen();

    // ---- Loupe --------------------------------------------------------------------------------

    /// <summary>
    /// How long the button must be down before the loupe opens.
    ///
    /// Not a throttle — it is what keeps press-and-hold and double-click-to-open-fullscreen out of
    /// each other's way. Both gestures start with the same press, and without this the loupe flashes
    /// on screen every time the preview is double-clicked. Short enough that a deliberate hold still
    /// feels immediate.
    /// </summary>
    private static readonly TimeSpan LoupeHoldDelay = TimeSpan.FromMilliseconds(150);

    private readonly TranslateTransform _loupePosition = new();

    private DispatcherTimer? _loupeHoldTimer;

    /// <summary>Where in the picture the pointer went down, kept while waiting out the hold delay.</summary>
    private Point? _loupeRelative;

    /// <summary>
    /// True while Inspect is on: the loupe stays open and follows the pointer with no button held,
    /// for working through a picture rather than glancing at one spot.
    /// </summary>
    private bool _loupePinned;

    /// <summary>
    /// Turns Inspect on or off. Pinning shows the loupe straight away, at the pointer if it is over
    /// the picture and in the middle otherwise, so the menu selection has a visible result either way.
    /// </summary>
    private void OnToggleInspect(object? sender, RoutedEventArgs e)
    {
        if (_loupePinned)
        {
            StopInspecting();
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel || viewModel.LoupeSource is null)
        {
            return;
        }

        _loupePinned = true;
        PreviewLoupe.IsPinned = true;
        InspectMenuItem.Header = "Stop Inspecting";

        // A RAW may not be decoded yet; Inspect is a deliberate request to look closely, so ask now.
        _ = viewModel.EnsureFullPreviewAsync();

        _loupeRelative ??= new Point(0.5, 0.5);
        PreviewLoupe.Relative = _loupeRelative.Value;
        PreviewLoupe.TargetWidth = viewModel.LoupeTargetWidth;
        PlaceLoupe(new Point(
            PreviewImage.Bounds.Width * _loupeRelative.Value.X,
            PreviewImage.Bounds.Height * _loupeRelative.Value.Y));

        PreviewLoupe.IsVisible = true;
    }

    private void StopInspecting()
    {
        _loupePinned = false;
        PreviewLoupe.IsPinned = false;
        InspectMenuItem.Header = "Inspect";
        HideLoupe();
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // While pinned the loupe is already following the pointer; a click should not start a second,
        // press-and-hold gesture on top of it.
        if (_loupePinned)
        {
            return;
        }

        if (!e.GetCurrentPoint(PreviewImage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!TrackLoupe(e))
        {
            return;
        }

        // Held for the duration of the gesture, so sliding off the image — or off the window —
        // still delivers the release that closes the loupe.
        e.Pointer.Capture(PreviewImage);

        // A RAW has not been decoded yet: ask now, and say so. Until it lands the loupe magnifies
        // the preview, so there is something useful on screen throughout.
        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.EnsureFullPreviewAsync();
        }

        _loupeHoldTimer?.Stop();
        _loupeHoldTimer = new DispatcherTimer { Interval = LoupeHoldDelay };
        _loupeHoldTimer.Tick += (_, _) =>
        {
            _loupeHoldTimer?.Stop();

            if (_loupeRelative is not null)
            {
                PreviewLoupe.IsVisible = true;
            }
        };
        _loupeHoldTimer.Start();
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_loupePinned || _loupeRelative is not null)
        {
            TrackLoupe(e);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_loupePinned)
        {
            HideLoupe();
        }
    }

    private void OnPreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_loupePinned)
        {
            HideLoupe();
        }
    }

    /// <summary>
    /// Points the loupe at wherever the pointer is. Returns false when that is not over the picture —
    /// the preview is letterboxed, and there is nothing to magnify in the margins.
    /// </summary>
    private bool TrackLoupe(PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || viewModel.Preview is not { } preview
            || PreviewImage.Bounds.Width <= 0)
        {
            return false;
        }

        var viewport = PreviewImage.Bounds.Size;
        var content = new Size(preview.PixelSize.Width, preview.PixelSize.Height);
        var pointer = e.GetPosition(PreviewImage);

        if (LoupeGeometry.ToRelative(pointer, content, viewport) is not { } relative)
        {
            // Off the picture. While pinned the loupe stays where it was rather than blinking out
            // every time the pointer crosses the letterbox on its way somewhere else.
            if (!_loupePinned)
            {
                HideLoupe();
            }

            return false;
        }

        _loupeRelative = relative;
        PreviewLoupe.Relative = relative;
        PreviewLoupe.TargetWidth = viewModel.LoupeTargetWidth;

        PlaceLoupe(pointer);

        PreviewLoupe.Caption = DescribeMagnification(viewModel, content, viewport, RenderScaling);
        return true;
    }

    /// <summary>Centred on the pointer, then kept inside the pane so it is never half off the edge.</summary>
    private void PlaceLoupe(Point pointer)
    {
        var viewport = PreviewImage.Bounds.Size;

        _loupePosition.X = Math.Clamp(pointer.X - PreviewLoupe.Width / 2, 0, Math.Max(0, viewport.Width - PreviewLoupe.Width));
        _loupePosition.Y = Math.Clamp(pointer.Y - PreviewLoupe.Height / 2, 0, Math.Max(0, viewport.Height - PreviewLoupe.Height));
    }

    /// <summary>
    /// "100%" only once the full-resolution decode is in hand. Before that the loupe is magnifying
    /// the preview, and calling that 100% would claim a pixel-level look at the photograph that it
    /// is not.
    /// </summary>
    private static string DescribeMagnification(
        MainWindowViewModel viewModel, Size content, Size viewport, double renderScaling)
    {
        if (viewModel.LoupeSource is null)
        {
            return string.Empty;
        }

        // Against the target width, so the figure does not change when a develop lands — only the
        // word in front of it does.
        var magnification = LoupeGeometry.Magnification(
            viewModel.LoupeTargetWidth, content, viewport, renderScaling);

        return viewModel.IsLoupeFullResolution
            ? $"Developed · 100% · {magnification:F1}×"
            : $"Preview · {magnification:F1}×";
    }

    private void HideLoupe()
    {
        _loupeHoldTimer?.Stop();
        _loupeHoldTimer = null;
        _loupeRelative = null;
        PreviewLoupe.IsVisible = false;
    }

    /// <summary>
    /// Double-clicking a tile opens it, which is what double-clicking a thumbnail means everywhere
    /// else. The single click that precedes it has already selected the item.
    /// </summary>
    private void OnThumbnailDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenFullscreen();
        e.Handled = true;
    }

    /// <summary>
    /// Opens the right-clicked item, selecting it first: right-clicking a tile that was not already
    /// selected should show that tile, not whatever happened to be selected before.
    /// </summary>
    private void OnFullscreenFromMenu(object? sender, RoutedEventArgs e)
    {
        if (ItemFor(sender) is { } item && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectedItem = item;
        }

        OpenFullscreen();
    }

    /// <summary>The item a context-menu click came from; the menu carries the tile's DataContext.</summary>
    private MediaItemViewModel? ItemFor(object? sender)
        => (sender as Control)?.DataContext as MediaItemViewModel
           ?? (DataContext as MainWindowViewModel)?.SelectedItem;

    /// <summary>
    /// Copies the right-clicked tile's keywords, which need not be the selected one — the menu
    /// carries its own DataContext, so it works on a tile that was never clicked.
    /// </summary>
    private async void OnCopyKeywords(object? sender, RoutedEventArgs e)
    {
        if (ItemFor(sender) is { } item && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.Inspector.CopyKeywordsFromAsync(item.File);
        }
    }

    /// <summary>
    /// Reveals the right-clicked item, which is not necessarily the selected one — the context menu
    /// carries its own DataContext, so it works even on a tile that was never clicked.
    /// </summary>
    private void OnRevealInFileManager(object? sender, RoutedEventArgs e)
    {
        var item = ItemFor(sender);

        if (item is not null)
        {
            RevealInFileManager.Reveal(item.File.FullPath);
        }
    }

    /// <summary>The folder a tree context-menu click came from, or null for a placeholder row.</summary>
    private static FolderNodeViewModel? FolderFor(object? sender)
        => (sender as Control)?.DataContext as FolderNodeViewModel is { IsPlaceholder: false } folder
            ? folder
            : null;

    /// <summary>
    /// Suppresses the menu on the placeholder child that gives an unexpanded folder its arrow. It
    /// stands for contents nobody has read yet and has no path, so there is nothing to open.
    ///
    /// Handled on the row rather than in the menu's own Opening event, which is where this started:
    /// a ContextMenu declared inside a template has no DataContext yet when Opening fires, so the
    /// guard saw null for every folder and cancelled the lot. The row always knows what it is.
    /// </summary>
    private void OnFolderContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (FolderFor(sender) is null)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the right-clicked folder, which need not be the selected one — the menu carries the
    /// row's DataContext, so it works on a folder that was never clicked into.
    /// </summary>
    private void OnOpenFolderInFileManager(object? sender, RoutedEventArgs e)
    {
        if (FolderFor(sender) is { } folder)
        {
            RevealInFileManager.OpenFolder(folder.FullPath);
        }
    }

    private void OnFullscreen(object? sender, RoutedEventArgs e) => OpenFullscreen();

    private void OnTransportFullscreen(object? sender, EventArgs e) => OpenFullscreen();

    /// <summary>
    /// Opens the current selection for inspection. Video keeps playing into the fullscreen surface
    /// because the player pushes frames to whoever is listening, rather than owning one view.
    /// </summary>
    private void OpenFullscreen()
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedItem is null)
        {
            return;
        }

        // Shown without an owner: macOS refuses to take a child window fullscreen, so an owned
        // viewer silently stays a normal window.
        // Attached before showing, so it knows whether to open fullscreen or maximised. Shown
        // without an owner: macOS refuses to take a child window fullscreen, and an owned viewer
        // would silently stay a normal window.
        var window = new MediaViewerWindow();
        window.Attach(viewModel);
        window.Show();
    }

    private void OnOpenFolder(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenFolderCommand.CanExecute(null))
        {
            viewModel.OpenFolderCommand.Execute(null);
        }
    }

    private void OnOpenSettings(object? sender, EventArgs e) => _ = OpenSettingsAsync();

    /// <summary>
    /// Public because the macOS application menu lives on <see cref="App"/>, which has no other way
    /// to reach the window that must own the dialog.
    /// </summary>
    /// <param name="tab">
    /// The tab to open on, when the request came from somewhere that asked for one thing in
    /// particular — the inspector's keyword and label areas. Null leaves Settings where it normally
    /// starts, which is what the menu item and the shortcut want.
    /// </param>
    public async Task OpenSettingsAsync(SettingsTab? tab = null)
    {
        if (SettingsViewModelFactory is not { } factory)
        {
            return;
        }

        var viewModel = factory();

        // So importing keywords can be scoped to what is open rather than the whole catalog.
        viewModel.WorkspacePath = (DataContext as MainWindowViewModel)?.WorkspacePath;

        var window = new SettingsWindow { DataContext = viewModel };

        if (tab is { } wanted)
        {
            window.ShowTab(wanted);
        }

        await window.ShowDialog(this);
    }

    // ---- Search field suggestions ----------------------------------------------------------------

    /// <summary>
    /// Set while the box is being rewritten by accepting a suggestion. Without it, writing
    /// "keyword:" back into the box looks exactly like the user typing a colon, and the list
    /// immediately reopens on the field that was just chosen.
    /// </summary>
    private bool _acceptingSuggestion;

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_acceptingSuggestion || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateFieldSuggestions(SearchBox.Text, SearchBox.CaretIndex);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!viewModel.HasFieldSuggestions)
        {
            // Enter is handled here rather than as a KeyBinding, because a KeyBinding cannot stand
            // aside while the list is open.
            if (e.Key == Key.Enter)
            {
                viewModel.SearchCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSuggestion(1);
                break;

            case Key.Up:
                viewModel.MoveSuggestion(-1);
                break;

            case Key.Escape:
                viewModel.DismissFieldSuggestions();
                break;

            case Key.Enter or Key.Tab:
                AcceptSuggestion(viewModel);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnSuggestionClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            AcceptSuggestion(viewModel);
        }
    }

    private void AcceptSuggestion(MainWindowViewModel viewModel)
    {
        if (viewModel.SelectedSuggestionIndex < 0 ||
            viewModel.SelectedSuggestionIndex >= viewModel.FieldSuggestions.Count)
        {
            return;
        }

        var chosen = viewModel.FieldSuggestions[viewModel.SelectedSuggestionIndex];

        // A field completes to "name:" and leaves the caret ready for the value; a value completes
        // in place, keeping any alternatives already listed in the same term.
        var (text, caret) = chosen.IsField
            ? SearchSuggestion.AcceptField(SearchBox.Text, SearchBox.CaretIndex, chosen.Text.TrimEnd(':'))
            : SearchSuggestion.AcceptValue(SearchBox.Text, SearchBox.CaretIndex, chosen.Text);

        _acceptingSuggestion = true;

        try
        {
            viewModel.DismissFieldSuggestions();
            SearchBox.Text = text;
            SearchBox.CaretIndex = caret;
        }
        finally
        {
            _acceptingSuggestion = false;
        }

        // The flag alone is not enough. Text is bound two-way, so writing "rating:" into the box
        // also travels out to the ViewModel and back, and that return trip raises TextChanged again
        // after the flag has been cleared — which looks exactly like the user typing a colon, and
        // reopens the list on the field just chosen. Posting the dismissal puts it after anything
        // the binding still has to do.
        Dispatcher.UIThread.Post(
            () =>
            {
                // Completing a field leaves the caret after a colon, which is a new question rather
                // than an answer — so offer that field's values instead of closing.
                if (chosen.IsField)
                {
                    viewModel.UpdateFieldSuggestions(SearchBox.Text, SearchBox.CaretIndex);
                }
                else
                {
                    viewModel.DismissFieldSuggestions();
                }
            },
            DispatcherPriority.Background);

        // The click that chose a field landed on the list, so the box has to be given back the
        // caret or the user is left typing into nothing.
        SearchBox.Focus();
    }

    /// <summary>
    /// Brings the view up to date with anything edited elsewhere while the window was in the
    /// background. Fire and forget: nothing waits on it, and the view model declines to do the work
    /// if it has only just done it.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.RefreshExternalChangesAsync();
        }
    }

    /// <summary>
    /// Reads the workspace's keywords as the filter popup opens, so the list is there the first
    /// time it is looked at rather than one open behind.
    /// </summary>
    private void OnFilterFlyoutOpened(object? sender, EventArgs e)
        => (DataContext as MainWindowViewModel)?.PrepareFilters();
}
