using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Amberglass.UI.Controls;

/// <summary>
/// Hosts one piece of content and lets it be zoomed and panned.
///
/// Deliberately content-agnostic: it transforms whatever child it is given, so a still and a video
/// surface both become inspectable through the same control rather than each growing its own zoom
/// implementation. That is also what makes side-by-side comparison a matter of placing two of these
/// beside each other later.
///
/// The child is laid out at its natural size and moved with a render transform, so zooming costs a
/// transform rather than a layout pass, and scale means what it says: at 1, one content pixel covers
/// one screen pixel.
/// </summary>
public class ZoomPanViewer : Decorator
{
    /// <summary>The content's intrinsic pixel size. Zoom is meaningless without it.</summary>
    public static readonly StyledProperty<Size> NaturalSizeProperty =
        AvaloniaProperty.Register<ZoomPanViewer, Size>(nameof(NaturalSize));

    /// <summary>Current magnification, published so the UI can show it and offer a reset.</summary>
    public static readonly DirectProperty<ZoomPanViewer, double> ScaleProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanViewer, double>(nameof(Scale), o => o.Scale);

    public static readonly DirectProperty<ZoomPanViewer, bool> IsFittedProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanViewer, bool>(nameof(IsFitted), o => o.IsFitted);

    private readonly ZoomState _state = new();
    private readonly TranslateTransform _translate = new();
    private readonly ScaleTransform _scale = new();
    private readonly TransformGroup _transforms = new();

    private Point? _dragOrigin;
    private Point _dragStartOffset;
    private double _scaleValue = 1;
    private bool _isFitted = true;

    static ZoomPanViewer()
    {
        AffectsMeasure<ZoomPanViewer>(NaturalSizeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ZoomPanViewer>(true);
        FocusableProperty.OverrideDefaultValue<ZoomPanViewer>(true);
    }

    public ZoomPanViewer()
    {
        // Scale before translate: the offset is in viewport pixels, so it must not itself be scaled.
        _transforms.Children.Add(_scale);
        _transforms.Children.Add(_translate);
    }

    public Size NaturalSize
    {
        get => GetValue(NaturalSizeProperty);
        set => SetValue(NaturalSizeProperty, value);
    }

    public double Scale
    {
        get => _scaleValue;
        private set => SetAndRaise(ScaleProperty, ref _scaleValue, value);
    }

    public bool IsFitted
    {
        get => _isFitted;
        private set => SetAndRaise(IsFittedProperty, ref _isFitted, value);
    }

    public void Fit()
    {
        _state.Fit();
        Apply();
    }

    public void ActualSize()
    {
        _state.ActualSize();
        Apply();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The child is measured at its natural size, not the viewport's: it is the transform that
        // decides how much of it is on screen.
        Child?.Measure(NaturalSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(NaturalSize));

        _state.SetContent(NaturalSize, finalSize);
        Apply();

        return finalSize;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!_state.HasContent)
        {
            return;
        }

        // Trackpads report fractional deltas; raising the step to that power keeps a slow two-finger
        // scroll smooth instead of jumping a whole notch at a time.
        var factor = Math.Pow(ZoomState.WheelStep, e.Delta.Y);

        _state.ZoomBy(factor, e.GetPosition(this));
        Apply();

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_state.IsFitted || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragStartOffset = _state.Offset;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragOrigin is not { } origin)
        {
            return;
        }

        // Measured from where the drag started rather than accumulated per move, so rounding cannot
        // make the image creep away from the pointer over a long drag.
        var current = e.GetPosition(this);
        _state.PanBy(_dragStartOffset + (current - origin) - _state.Offset);
        Apply();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(e.Pointer);
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_dragOrigin is null)
        {
            return;
        }

        _dragOrigin = null;
        Cursor = Cursor.Default;
        pointer?.Capture(null);
    }

    private void Apply()
    {
        // The transform belongs on the child. On this control it would move the clip with it, and
        // the content would never be cropped to the viewport.
        if (Child is { } child && !ReferenceEquals(child.RenderTransform, _transforms))
        {
            child.RenderTransformOrigin = RelativePoint.TopLeft;
            child.RenderTransform = _transforms;
        }

        _scale.ScaleX = _state.Scale;
        _scale.ScaleY = _state.Scale;
        _translate.X = _state.Offset.X;
        _translate.Y = _state.Offset.Y;

        Scale = _state.Scale;
        IsFitted = _state.IsFitted;
    }
}
