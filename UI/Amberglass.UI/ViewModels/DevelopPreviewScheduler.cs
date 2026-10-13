namespace Amberglass.UI.ViewModels;

/// <summary>
/// Decides when a change to the develop settings is shown, and how: a quick draft first, then the
/// real develop once the settings stop moving.
///
/// A full develop takes from half a second to several, and while a slider was being dragged nothing
/// on screen changed at all — the develop waited for the dragging to stop, and then for itself.
/// Lightroom's answer, and this one, is to show a cheap rendering of each new setting while the hand
/// is still moving and keep the expensive one for when it rests.
///
/// <list type="bullet">
/// <item>A dragged change waits <see cref="DraftDelay"/> for the next tick, then draws a draft. Each
/// new change abandons the draft before it.</item>
/// <item>Once nothing has changed for <see cref="SettleDelay"/>, the full develop runs.</item>
/// <item>A change that is picked rather than dragged — a menu, a reset — has no ticks to wait for:
/// its draft starts at once, and the full develop straight after.</item>
/// </list>
///
/// Pulled out of the ViewModel so the timing can be tested; the work itself is handed in.
/// </summary>
public sealed class DevelopPreviewScheduler
{
    /// <summary>Short: just long enough that a drag's ticks do not each start a develop.</summary>
    public static readonly TimeSpan DefaultDraftDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long the settings must rest before the full develop. Long enough that a pause mid-drag
    /// does not start seconds of work only to throw it away; short enough to feel like letting go.
    /// </summary>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(700);

    private readonly Func<CancellationToken, Task> _draft;
    private readonly Func<Task> _full;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pending;

    /// <param name="draft">Renders and shows a draft; cancelled when it has been overtaken.</param>
    /// <param name="full">Starts the full develop, which manages its own cancellation.</param>
    public DevelopPreviewScheduler(
        Func<CancellationToken, Task> draft,
        Func<Task> full,
        TimeSpan? draftDelay = null,
        TimeSpan? settleDelay = null)
    {
        _draft = draft;
        _full = full;
        DraftDelay = draftDelay ?? DefaultDraftDelay;
        SettleDelay = settleDelay ?? DefaultSettleDelay;
    }

    public TimeSpan DraftDelay { get; }

    public TimeSpan SettleDelay { get; }

    /// <summary>The settings changed. Supersedes anything still waiting from an earlier change.</summary>
    /// <param name="dragged">True for a slider, whose next tick is probably already on its way.</param>
    public void Changed(bool dragged)
    {
        CancellationTokenSource current;

        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = current = new CancellationTokenSource();
        }

        _ = RunAsync(dragged, current.Token);
    }

    /// <summary>Abandons whatever is waiting, for when the picture it was meant for has gone.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task RunAsync(bool dragged, CancellationToken cancellationToken)
    {
        try
        {
            if (dragged)
            {
                await Task.Delay(DraftDelay, cancellationToken).ConfigureAwait(false);
            }

            // Not awaited: the draft takes a few hundred milliseconds, and the settle timer has to
            // keep counting from the change rather than from whenever the draft happens to finish.
            var draft = _draft(cancellationToken);

            if (dragged)
            {
                await Task.Delay(Max(SettleDelay - DraftDelay, TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A pick has no ticks behind it, but the draft still gets to land first: it is a
                // fraction of the full develop's cost and is on screen while that one runs.
                await draft.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _full().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
