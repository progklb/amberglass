namespace Amberglass.UI.ViewModels;

/// <summary>
/// Bookkeeping for the full-resolution decode: what has been asked for, what has been delivered, and
/// what pixels are currently held.
///
/// Pulled out of the ViewModel because getting it wrong is invisible until it is infuriating. The
/// first version tracked only "what did we last ask for", which conflated two different questions —
/// *is a decode already running for this?* and *do we already have this?* — into one field. Any run
/// that ended without delivering (the selection moved, the decode was cancelled, the file turned out
/// to be a video) left that field set with nothing behind it, and every later request was then
/// dismissed as redundant. The picture stayed on its embedded JPEG until something happened to clear
/// the field by hand.
///
/// Three separate pieces of state, so no single one has to mean two things:
/// <list type="bullet">
/// <item><see cref="InFlight"/> — a decode is running for this file. Cleared when the run ends,
/// however it ends.</item>
/// <item><see cref="Delivered"/> — a decode has produced pixels for this file under the current
/// rendering. Cleared when the rendering changes.</item>
/// <item><see cref="Held"/> — which file the pixels in hand belong to, which is what decides whether
/// they should be thrown away before the next decode.</item>
/// </list>
/// </summary>
internal sealed class FullPreviewTracker
{
    /// <summary>The file a decode is currently running for, or null when none is.</summary>
    public string? InFlight { get; private set; }

    /// <summary>The file whose decode has completed under the current rendering settings.</summary>
    public string? Delivered { get; private set; }

    /// <summary>The file the bitmap in hand belongs to.</summary>
    public string? Held { get; private set; }

    /// <summary>
    /// Whether a decode is worth starting: not already running, and not already done.
    ///
    /// Both halves matter. Without the first, selecting an item — which raises several properties,
    /// each asking the viewer to refresh — would start and cancel the same expensive develop two or
    /// three times. Without the second, every refresh after it finished would develop it again.
    /// </summary>
    public bool ShouldStart(string? wanted) => InFlight != wanted && Delivered != wanted;

    /// <summary>
    /// Whether the pixels in hand are of a different photograph, and so must go before the next
    /// decode. False when the same file is merely being rendered again — a re-develop, or a switch
    /// between RAW and embedded JPEG — where dropping to a lower-quality rendition for several
    /// seconds is exactly what makes the comparison useless.
    /// </summary>
    public bool IsChangingFile(string? wanted) => Held != wanted;

    public void Begin(string? wanted) => InFlight = wanted;

    /// <summary>Pixels have arrived.</summary>
    public void Delivering(string? wanted)
    {
        Delivered = wanted;
        Held = wanted;
    }

    /// <summary>
    /// The run has ended — delivered or not, cancelled or not. Clearing the marker here is what
    /// makes the guard self-healing: a run that produced nothing leaves no trace to suppress the
    /// next attempt.
    ///
    /// Only clears its own run, so a later request that has already started is not disturbed by an
    /// older one finishing late.
    /// </summary>
    public void Ended(string? wanted)
    {
        if (InFlight == wanted)
        {
            InFlight = null;
        }
    }

    /// <summary>The pixels have been released.</summary>
    public void Forget()
    {
        Delivered = null;
        Held = null;
    }

    /// <summary>
    /// The way the file is rendered has changed, so whatever is held no longer answers the question
    /// and any decode in flight is producing the wrong thing.
    ///
    /// Deliberately leaves <see cref="Held"/> alone: the pixels stay on screen while the new
    /// rendering is produced, which is the whole point of being able to compare them.
    /// </summary>
    public void Invalidate()
    {
        InFlight = null;
        Delivered = null;
    }
}
