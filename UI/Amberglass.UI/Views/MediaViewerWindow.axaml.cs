using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Amberglass.Core.Models;
using Amberglass.UI.Controls;
using Amberglass.UI.ViewModels;

namespace Amberglass.UI.Views;

/// <summary>
/// Fullscreen inspection of the current selection.
///
/// A separate window rather than a mode of the main one: the main window keeps its layout untouched,
/// and closing needs nothing restored. It shares the main ViewModel, so browsing here moves the
/// selection there too and the two never disagree about what is being looked at.
/// </summary>
public partial class MediaViewerWindow : Window
{
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long the strip takes to fade. Matches the transition declared on the Hint border in the
    /// markup; the two have to agree, and there is no way to say so in one place.
    /// </summary>
    private static readonly TimeSpan HintFadeDuration = TimeSpan.FromSeconds(0.8);

    private MainWindowViewModel? _viewModel;
    private DispatcherTimer? _hintTimer;

    /// <summary>
    /// True until the view is first adjusted by hand. The window is created at an ordinary size and
    /// only then goes fullscreen, so the first fit is computed against the wrong viewport; refitting
    /// on resize corrects it. Deliberately stops at the first interaction, because after that the
    /// magnification is the user's and resizing must not discard it.
    /// </summary>
    private bool _awaitingInitialFit = true;

    public MediaViewerWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closed += OnClosed;

        // Tunnelled: a focused chrome button would otherwise swallow Space and re-trigger itself
        // instead of resetting the view. Safe here because the viewer has no text input to type in.
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);

        SizeChanged += (_, _) =>
        {
            if (_awaitingInitialFit)
            {
                Viewer.Fit();
            }
        };
    }

    /// <summary>Must be called before showing: the window sizes itself from these settings.</summary>
    public void Attach(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Player.FrameReady += OnFrameReady;
        viewModel.Player.SurfaceCleared += OnSurfaceCleared;

        ShowCurrent();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        FillScreen();
        Viewer.Focus();

        // Shown, then faded. The transition on the border animates the change.
        _hintTimer = new DispatcherTimer { Interval = HintDuration };
        _hintTimer.Tick += (_, _) =>
        {
            // Reading it is a reason to keep it. The timer is left running rather than stopped, so
            // it asks again in another few seconds and fades once the pointer has moved off.
            if (_hintHovered)
            {
                return;
            }

            Hint.Opacity = 0;
            _hintTimer?.Stop();

            // It is still on screen while it fades, so hovering during that brings it back. Only
            // once it has actually gone does hovering stop meaning anything.
            DispatcherTimer.RunOnce(() => _hintGone = true, HintFadeDuration);
        };
        _hintTimer.Start();

        // Tunnelled, and the strip itself stays untouchable: making it hit-testable would have it
        // swallow a drag that began over it, and dragging is how the picture is panned. Watching
        // where the pointer is costs nothing and takes nothing away from the viewer.
        AddHandler(PointerMovedEvent, OnPointerMovedOverHint, RoutingStrategies.Tunnel);
    }

    /// <summary>True while the pointer is within the hint strip, whether or not it can be clicked.</summary>
    private bool _hintHovered;

    /// <summary>
    /// Set once the strip has faded. Hovering keeps it, and brings it back while it is still on its
    /// way out, but does not resurrect it afterwards — a strip that reappeared whenever the pointer
    /// crossed a patch of empty screen would be worse than one that goes.
    /// </summary>
    private bool _hintGone;

    private void OnPointerMovedOverHint(object? sender, PointerEventArgs e)
    {
        if (_hintGone || _hintTimer is null)
        {
            return;
        }

        // Relative to the strip, so there is no parent chain to walk and no scrolling to account for.
        var point = e.GetPosition(Hint);

        var over = point.X >= 0 && point.Y >= 0
            && point.X <= Hint.Bounds.Width && point.Y <= Hint.Bounds.Height;

        if (over == _hintHovered)
        {
            return;
        }

        _hintHovered = over;

        if (over)
        {
            // Full strength again: it may already have started fading when the pointer arrived.
            Hint.Opacity = 1;
        }

        // Either way the clock starts again, so it lingers for the usual few seconds after the
        // pointer leaves rather than vanishing the moment it does.
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    /// <summary>
    /// Fills the screen, one of two ways.
    ///
    /// **Fullscreen** hides the menu bar, but on macOS that means a Space of its own — an animation
    /// and a context switch every time, which is a lot of ceremony for a look at one photo.
    /// **Maximised** stays on the current Space and leaves the menu bar showing. Neither is
    /// obviously right, so it is a setting.
    ///
    /// Two things are needed for real fullscreen and both are easy to get wrong silently: the window
    /// must keep its system decorations (macOS will not take an undecorated window fullscreen), and
    /// it must not be owned by another window (a child window is refused too). In either case the
    /// request is ignored and the window simply stays the size it was.
    /// </summary>
    private void FillScreen()
    {
        // Positioned on the screen the main window is on, so it fills that display rather than
        // wherever it happened to open.
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            Position = screen.Bounds.Position;
        }

        var state = _viewModel?.ViewerOpensFullscreen == true
            ? WindowState.FullScreen
            : WindowState.Maximized;

        // Posted rather than set inline: assigning the state during Opened is ignored, the window
        // having only just been created natively.
        Dispatcher.UIThread.Post(() => WindowState = state, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();

        if (_viewModel is not { } viewModel)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelChanged;
        viewModel.Player.FrameReady -= OnFrameReady;
        viewModel.Player.SurfaceCleared -= OnSurfaceCleared;

        // Tens of megabytes, and nothing is looking at it now. Released rather than merely discarded
        // so the main window's loupe can ask for it again on this same file.
        Still.Source = null;
        viewModel.ReleaseFullPreview();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Preview)
            or nameof(MainWindowViewModel.SelectedItem)
            or nameof(MainWindowViewModel.IsVideoSelected))
        {
            ShowCurrent();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.FullPreview) && _viewModel?.IsVideoSelected == false)
        {
            // The full-size decode has landed; swap it in without disturbing the view.
            ShowStill(_viewModel.FullPreview ?? _viewModel.Preview, isNewItem: false);
        }
    }

    /// <summary>Points the viewer at whatever is selected now, refitting for the new content.</summary>
    private void ShowCurrent()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        UpdateCounter();
        HintText.ItemsSource = ViewerShortcuts.Hint(viewModel.IsVideoSelected);

        if (viewModel.IsVideoSelected)
        {
            Still.IsVisible = false;
            Surface.IsVisible = true;

            // Frames are pushed, so a surface created after the last one was sent has nothing to
            // show. Ask for the current frame again rather than waiting for playback.
            _ = viewModel.Player.RefreshFrameAsync();
            return;
        }

        Surface.IsVisible = false;
        Still.IsVisible = true;

        // The full-size render if there is one for this file, otherwise the cached preview, which is
        // already in memory and appears instantly. Preferring the full one is what stops a
        // re-develop flashing back to a lower-quality rendition of the same picture.
        ShowStill(viewModel.FullPreview ?? viewModel.Preview, isNewItem: true);

        _ = viewModel.EnsureFullPreviewAsync();
    }

    /// <summary>
    /// Points the viewer at a bitmap and sizes it from that bitmap's pixels, so 100% means one image
    /// pixel per screen pixel — which is only true once the full-size decode has arrived.
    /// </summary>
    /// <param name="isNewItem">
    /// True when this is a different photograph, which starts fitted. False when it is another
    /// rendering of the same one — a finished develop, or a switch between RAW and embedded JPEG —
    /// where the zoom and position are what the comparison is being made at and must survive.
    /// </param>
    /// <summary>
    /// Whether the still is being shown grey. A view setting, not an edit: it survives moving
    /// between photographs, because comparing a set in black and white means seeing all of them
    /// that way, and nothing about it reaches the file.
    /// </summary>
    private bool _blackAndWhite;

    /// <summary>The colour bitmap on show, kept so the toggle can go back to it.</summary>
    private Bitmap? _colourStill;

    /// <summary>Its grey copy, made on demand and disposed when the picture changes.</summary>
    private Bitmap? _greyStill;

    private void ShowStill(Bitmap? image, bool isNewItem)
    {
        // A different rendering means the old grey copy is of the wrong picture.
        if (!ReferenceEquals(_colourStill, image))
        {
            _greyStill?.Dispose();
            _greyStill = null;
            _colourStill = image;
        }

        Still.Source = _blackAndWhite ? GreyFor(image) : image;

        if (image is null)
        {
            return;
        }

        var size = new Size(image.PixelSize.Width, image.PixelSize.Height);
        if (Viewer.NaturalSize == size)
        {
            return;
        }

        var wasFitted = Viewer.IsFitted;
        Viewer.NaturalSize = size;

        // Fit a new picture, or one being looked at whole. Otherwise leave the view alone: the
        // viewer keeps the same region framed across a change of resolution.
        if (isNewItem && (wasFitted || _awaitingInitialFit))
        {
            Viewer.Fit();
        }
    }

    /// <summary>The grey copy of the picture on show, made the first time it is asked for.</summary>
    private Bitmap? GreyFor(Bitmap? image)
    {
        if (image is null)
        {
            return null;
        }

        _greyStill ??= GreyscaleBitmap.From(image);

        // Falls back to colour rather than an empty window if the copy could not be made.
        return _greyStill ?? image;
    }

    /// <summary>
    /// Turns the black-and-white preview on or off. Only the still is affected; video keeps its
    /// colour, and the key that gets here is not offered while a video is on screen.
    /// </summary>
    private void ToggleBlackAndWhite()
    {
        if (_viewModel?.IsVideoSelected != false)
        {
            return;
        }

        _blackAndWhite = !_blackAndWhite;
        BlackAndWhiteButton.Classes.Set("on", _blackAndWhite);

        Still.Source = _blackAndWhite ? GreyFor(_colourStill) : _colourStill;
    }

    private void OnBlackAndWhite(object? sender, RoutedEventArgs e) => ToggleBlackAndWhite();

    private void UpdateCounter()
    {
        if (_viewModel is not { } viewModel || viewModel.SelectedItem is null)
        {
            Counter.IsVisible = false;
            return;
        }

        var index = viewModel.MediaItems.IndexOf(viewModel.SelectedItem);
        Counter.IsVisible = index >= 0;
        CounterLabel.Text = $"{index + 1} of {viewModel.MediaItems.Count}  ·  {viewModel.SelectedItem.FileName}";
    }

    private void OnFrameReady(VideoFrame frame)
    {
        var size = new Size(frame.Width, frame.Height);
        if (Viewer.NaturalSize != size)
        {
            Viewer.NaturalSize = size;
            Viewer.Fit();
        }

        Surface.Present(frame);
    }

    private void OnSurfaceCleared() => Surface.Clear();

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        _awaitingInitialFit = false;

        var isVideo = _viewModel?.IsVideoSelected == true;

        switch (ViewerShortcuts.Resolve(e.Key, isVideo))
        {
            case ViewerAction.Close:
                Close();
                break;

            case ViewerAction.Fit:
                Viewer.Fit();
                break;

            case ViewerAction.ActualSize:
                Viewer.ActualSize();
                break;

            case ViewerAction.Previous:
                _viewModel?.SelectPreviousCommand.Execute(null);
                break;

            case ViewerAction.Next:
                _viewModel?.SelectNextCommand.Execute(null);
                break;

            case ViewerAction.ToggleRawDevelopment:
                _viewModel?.ToggleRawDevelopmentCommand.Execute(null);
                break;

            case ViewerAction.TogglePlayback:
                _viewModel?.Player.TogglePlayCommand.Execute(null);
                break;

            case ViewerAction.ToggleBlackAndWhite:
                ToggleBlackAndWhite();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _awaitingInitialFit = false;

        if (e.ClickCount == 2)
        {
            if (Viewer.IsFitted)
            {
                Viewer.ActualSize();
            }
            else
            {
                Viewer.Fit();
            }

            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _awaitingInitialFit = false;
    }

    private void OnFit(object? sender, RoutedEventArgs e) => Viewer.Fit();

    private void OnActualSize(object? sender, RoutedEventArgs e) => Viewer.ActualSize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The transport's fullscreen button means "leave" when the viewer is what hosts it.</summary>
    private void OnLeaveFullscreen(object? sender, EventArgs e) => Close();
}
