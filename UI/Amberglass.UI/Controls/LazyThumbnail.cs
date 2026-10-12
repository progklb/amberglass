using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Amberglass.UI.ViewModels;

namespace Amberglass.UI.Controls;

/// <summary>
/// Triggers thumbnail generation only once its container is realized by the virtualizing panel.
/// This is what keeps opening a folder of tens of thousands of files cheap.
/// </summary>
public sealed class LazyThumbnail : Image
{
    /// <summary>
    /// Whether a thumbnail comes up like a print in the developer rather than appearing. Part of
    /// the contact sheet experiment, and set by a style from it, so the tile need not know.
    /// </summary>
    public static readonly StyledProperty<bool> DevelopsProperty =
        AvaloniaProperty.Register<LazyThumbnail, bool>(nameof(Develops));

    /// <summary>
    /// Long enough to be seen as a print coming up, short enough that a fast scroll through a
    /// cached folder reads as a wave rather than as a grid that is slow to draw.
    /// </summary>
    private static readonly TimeSpan DevelopDuration = TimeSpan.FromMilliseconds(560);

    /// <summary>The item this control last asked for, so its work can be cancelled on recycle.</summary>
    private MediaItemViewModel? _requested;

    private CancellationTokenSource? _developing;

    public bool Develops { get => GetValue(DevelopsProperty); set => SetValue(DevelopsProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The tile scrolled out of view or its container was recycled; stop generating for it.
        CancelOutstanding();
        StopDeveloping();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SourceProperty)
        {
            return;
        }

        // Only a first decode develops: the picture arriving where there was none. A recycled
        // container swapping one finished thumbnail for another is a scroll, not a decode, and a
        // grid that faded on every scroll would shimmer — which is a fault, not an effect.
        var firstDecode = change.OldValue is null && change.NewValue is IImage;

        if (firstDecode && Develops && this.GetVisualRoot() is not null)
        {
            Develop();
        }
        else
        {
            StopDeveloping();
        }
    }

    /// <summary>
    /// Brings the picture up from nothing. Opacity is all that can be animated here — Avalonia
    /// has no colour matrix, so the contrast a print gains in the tray cannot be — and it is
    /// enough: the eye reads a photograph emerging from the tile's dark ground as exactly that.
    /// </summary>
    private void Develop()
    {
        StopDeveloping();

        var cts = new CancellationTokenSource();
        _developing = cts;

        // Eased in as well as out. A print does nothing for a moment, then comes fast, then
        // settles; a plain ease-out would have it at half strength before the eye had arrived.
        var animation = new Animation
        {
            Duration = DevelopDuration,
            Easing = new QuadraticEaseInOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } }
            }
        };

        _ = animation.RunAsync(this, cts.Token);
    }

    /// <summary>
    /// Cancelled rather than left to finish. The animation only overrides the opacity while it
    /// runs, so stopping it leaves the local value — fully opaque — in force at once.
    /// </summary>
    private void StopDeveloping()
    {
        if (_developing is { } running)
        {
            _developing = null;
            running.Cancel();
            running.Dispose();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // A recycled container is reused for a different file: abandon the old one's work first.
        if (!ReferenceEquals(_requested, DataContext))
        {
            CancelOutstanding();
        }

        if (this.GetVisualRoot() is not null)
        {
            RequestThumbnail();
        }
    }

    private void RequestThumbnail()
    {
        if (DataContext is MediaItemViewModel item)
        {
            _requested = item;
            _ = item.EnsureThumbnailAsync();
        }
    }

    private void CancelOutstanding()
    {
        _requested?.CancelPendingThumbnail();
        _requested = null;
    }
}
