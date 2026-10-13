using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

public sealed partial class MediaItemViewModel : ObservableObject
{
    /// <summary>
    /// Grid thumbnails are always rendered and cached at this size regardless of the zoom slider,
    /// so changing the display size scales existing cache entries instead of regenerating them.
    /// </summary>
    public const int ThumbnailEdgePixels = 320;

    private readonly IThumbnailService _thumbnails;
    private int _thumbnailRequested;
    private CancellationTokenSource? _loadCts;

    public MediaItemViewModel(MediaFile file, IThumbnailService thumbnails)
    {
        File = file;
        _thumbnails = thumbnails;

        // From the scan's folder listing, or the catalog for a search result, so the grid marks
        // sidecars as it fills. The inspector corrects it when a file is opened.
        _hasSidecar = file.HasSidecar;
    }

    public MediaFile File { get; }

    public string FileName => File.FileName;

    public bool IsVideo => File.MediaType == MediaType.Video;

    public string SizeDisplay => ByteSize.Format(File.SizeBytes);

    /// <summary>
    /// This file's place on the sheet, printed in the rebate above it under the contact sheet
    /// experiment. Assigned when the item is added to the grid rather than derived from the
    /// collection, because a virtualised tile has no cheap way to ask where it is.
    /// </summary>
    [ObservableProperty]
    private int _frameNumber;

    /// <summary>
    /// What the rebate prints beside the frame number, where film would name its stock: the
    /// format, as its extension in capitals. "RAF", "ARW", "MOV" — the nearest thing a digital
    /// file has to a stock, and useful in a grid that mixes them.
    /// </summary>
    public string Stock => Path.GetExtension(File.FileName).TrimStart('.').ToUpperInvariant();

    public string ModifiedDisplay => File.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _thumbnailUnavailable;

    /// <summary>Drives the "modified" badge in the grid. Kept in sync with the pending-change store.</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>Embedded metadata and the sidecar disagree. Set when the item is inspected.</summary>
    [ObservableProperty]
    private bool _hasConflicts;

    /// <summary>An XMP sidecar exists next to this file.</summary>
    [ObservableProperty]
    private bool _hasSidecar;

    /// <summary>
    /// The rating, flag and label drawn on the tile.
    ///
    /// Held as one value rather than three properties because they always arrive together — from
    /// the catalog for a whole folder at once, or from an edit that may have touched any of them —
    /// and a single assignment cannot leave the tile showing two of the three.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingStars))]
    [NotifyPropertyChangedFor(nameof(HasRating))]
    [NotifyPropertyChangedFor(nameof(IsAccepted))]
    [NotifyPropertyChangedFor(nameof(IsRejected))]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    [NotifyPropertyChangedFor(nameof(HasMarks))]
    private MediaMarks _marks = MediaMarks.None;

    /// <summary>
    /// One entry per star earned, not five with some hollow.
    ///
    /// At tile size the empty ones are most of the ink and say nothing: three stars reads as three
    /// whether or not two ghosts follow it, and a grid of tiles is scanned, not studied.
    ///
    /// A list rather than a string of ★ characters, and drawn as a shape rather than set as text.
    /// The star has no glyph in the interface font, so it came from a fallback whose advance width
    /// measures short — the run sized itself to less than it drew and clipped the last star off,
    /// at five stars the one that matters most. Nothing outside the text could fix that, because
    /// the clipping happened inside the TextBlock's own bounds.
    /// </summary>
    public IReadOnlyList<int> RatingStars =>
        Marks.Rating is > 0 and var stars ? Enumerable.Range(1, stars).ToList() : [];

    public bool HasRating => Marks.Rating is > 0;

    public bool IsAccepted => Marks.Flag == MediaFlag.Accepted;

    public bool IsRejected => Marks.Flag == MediaFlag.Rejected;

    public bool HasFlag => IsAccepted || IsRejected;

    /// <summary>Whether the badge strip is worth any room at all.</summary>
    public bool HasMarks => HasRating || HasFlag;

    /// <summary>
    /// The label's colour, or null when there is no label. Set alongside <see cref="Marks"/> rather
    /// than worked out here, because resolving it needs the user's label library and a tile has no
    /// business knowing about settings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabel))]
    private string? _labelColour;

    public bool HasLabel => LabelColour is not null;

    /// <summary>The label's name, for the tooltip — the colour alone cannot say "Yellow".</summary>
    public string? LabelName => Marks.Label;

    // ---- Stacking -------------------------------------------------------------------------------

    /// <summary>
    /// The other files of this photograph, hidden behind this one. Empty unless the grid is
    /// collapsing pairs and this tile is the face of one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStacked))]
    [NotifyPropertyChangedFor(nameof(StackBadge))]
    [NotifyPropertyChangedFor(nameof(StackTip))]
    private IReadOnlyList<MediaFile> _stacked = [];

    public bool IsStacked => Stacked.Count > 0;

    /// <summary>
    /// What is underneath, in as little room as a tile has: the one companion's format, or how
    /// many there are when naming them would not fit.
    ///
    /// The format rather than a bare count, because the question a photographer asks of a stacked
    /// tile is "is the RAW there?", and "+CR2" answers it where "+1" does not.
    /// </summary>
    public string StackBadge => Stacked.Count switch
    {
        0 => string.Empty,
        1 => "+" + Path.GetExtension(Stacked[0].FileName).TrimStart('.').ToUpperInvariant(),
        var n => $"+{n}"
    };

    /// <summary>The names themselves, for the tooltip, where there is room to be exact.</summary>
    public string StackTip => Stacked.Count == 0
        ? string.Empty
        : "Also here: " + string.Join(", ", Stacked.Select(f => f.FileName));

    /// <summary>
    /// Requests the thumbnail once. Called when the item's container is realized, so opening a
    /// folder of 50,000 files only decodes the handful of tiles actually on screen.
    /// </summary>
    public async Task EnsureThumbnailAsync()
    {
        if (Thumbnail is not null || Interlocked.Exchange(ref _thumbnailRequested, 1) == 1)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Dispose();

        try
        {
            var bytes = await _thumbnails
                .GetThumbnailAsync(File, ThumbnailEdgePixels, ThumbnailPriority.Background, cts.Token)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ThumbnailUnavailable = true);
                return;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bitmap);
        }
        catch (OperationCanceledException)
        {
            // Scrolled out of view before it finished — allow a fresh attempt if it comes back.
            Interlocked.Exchange(ref _thumbnailRequested, 0);
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ThumbnailUnavailable = true);
        }
    }

    /// <summary>
    /// Abandons in-flight thumbnail work because the tile scrolled out of view. Without this, a fast
    /// scroll through a large folder leaves every tile it passed still queued and competing for the
    /// generator, which is exactly the work the user no longer cares about.
    ///
    /// A thumbnail that already arrived is kept — it costs nothing and makes scrolling back instant.
    /// </summary>
    public void CancelPendingThumbnail()
    {
        if (Thumbnail is not null)
        {
            return;
        }

        var cts = Interlocked.Exchange(ref _loadCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }

        Interlocked.Exchange(ref _thumbnailRequested, 0);
    }

}
