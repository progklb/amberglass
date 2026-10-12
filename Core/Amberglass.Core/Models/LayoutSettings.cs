namespace Amberglass.Core.Models;

/// <summary>
/// Where the panels were left.
///
/// Persisted because arranging the window is work, and work that has to be redone every launch is
/// not a preference but a chore: a photographer who likes large thumbnails and a wide inspector
/// means it for more than one session.
///
/// Every value is clamped on the way out rather than validated on the way in. The file can be
/// hand-edited, and a settings file is a poor reason for a window that cannot be used — a panel
/// three thousand pixels wide, or one nought pixels wide with no way to find it again, are both
/// worse than ignoring what was asked for.
/// </summary>
public sealed record LayoutSettings
{
    public static readonly LayoutSettings Default = new();

    public const double MinThumbnailSize = 64;
    public const double MaxThumbnailSize = 320;

    /// <summary>The widest a side panel may be left, so one cannot be dragged over the photographs.</summary>
    public const double MaxPanelWidth = 900;

    /// <summary>
    /// Narrower than this and a panel is collapsed rather than kept: below it the tree is a column
    /// of ellipses and the inspector cannot lay out its fields, so the space is doing nothing.
    /// </summary>
    public const double MinPanelWidth = 150;

    public double ThumbnailSize { get; init; } = 160;

    public double FolderPanelWidth { get; init; } = 240;

    public double InspectorWidth { get; init; } = 360;

    /// <summary>
    /// Collapsed panels are remembered along with the width they had, so reopening one puts it back
    /// where it was rather than at a default that is nobody's preference.
    /// </summary>
    public bool FolderPanelCollapsed { get; init; }

    public bool InspectorCollapsed { get; init; }

    /// <summary>
    /// The height of the lower of the two content panes — whichever one that is. The upper pane
    /// fills whatever is left, so one number describes the split either way round.
    ///
    /// The filmstrip is not stored here. It has its own setting and it overrides both heights while
    /// it is on, so letting it write to this would lose the arrangement it was borrowed from.
    /// </summary>
    public double BottomPaneHeight { get; init; } = 280;

    /// <summary>
    /// Whether the preview sits above the thumbnails.
    ///
    /// Both arrangements are in use: thumbnails first reads as a contact sheet being worked
    /// through, preview first reads as one photograph with the shoot underneath it, which is how
    /// most editing applications are laid out.
    /// </summary>
    public bool PreviewOnTop { get; init; }

    public double ClampedThumbnailSize => Math.Clamp(ThumbnailSize, MinThumbnailSize, MaxThumbnailSize);

    public double ClampedFolderPanelWidth => Math.Clamp(FolderPanelWidth, MinPanelWidth, MaxPanelWidth);

    public double ClampedInspectorWidth => Math.Clamp(InspectorWidth, MinPanelWidth, MaxPanelWidth);

    /// <summary>
    /// A floor rather than the panel minimum: this pane can be squeezed down to a sliver and still
    /// be doing its job, and dragging it to nothing is the gesture for the filmstrip.
    /// </summary>
    public double ClampedBottomPaneHeight => Math.Clamp(BottomPaneHeight, 60, 4000);
}
