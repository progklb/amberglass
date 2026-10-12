using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Amberglass.UI.Controls;

/// <summary>
/// The video transport, shared by the inline preview and the fullscreen viewer so the two cannot
/// diverge.
/// </summary>
public partial class VideoTransport : UserControl
{
    public VideoTransport() => InitializeComponent();

    /// <summary>
    /// Raised by the fullscreen button. The host decides what it means: entering fullscreen from
    /// the main window, leaving it from the viewer.
    /// </summary>
    public event EventHandler? FullscreenRequested;

    private void OnFullscreenRequested(object? sender, RoutedEventArgs e)
        => FullscreenRequested?.Invoke(this, EventArgs.Empty);
}
