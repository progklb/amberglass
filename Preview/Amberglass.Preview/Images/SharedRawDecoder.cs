using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;

namespace Amberglass.Preview.Images;

/// <summary>
/// Makes sure a RAW is developed once, however many parts of the application ask for it.
///
/// The preview pane's thumbnail tier and the viewer's full-size decode both develop when a file's
/// embedded preview is too small, and they ask at the same moment — selecting a photograph and
/// opening the viewer are one gesture apart. Each used to start its own develop: two processes, or
/// two single-threaded ImageIO renders, producing the same pixels side by side. Here the second
/// request joins the first, and a request arriving just after a develop finishes is handed its
/// result rather than starting another.
///
/// Callers share the <see cref="DecodedImage"/> they are given, so they must treat its pixels as
/// read-only.
/// </summary>
public sealed class SharedRawDecoder : IRawDecoder
{
    /// <summary>
    /// How long a finished develop is kept for latecomers. Long enough to bridge the pane and the
    /// viewer asking a beat apart; short enough that a 190 MB panorama is not held for nothing.
    /// </summary>
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromSeconds(15);

    private readonly IRawDecoder _inner;
    private readonly ISettingsService _settings;
    private readonly TimeSpan _retention;
    private readonly Lock _gate = new();
    private readonly Dictionary<DevelopKey, InFlight> _inFlight = [];

    private (DevelopKey Key, DecodedImage Image)? _recent;

    public SharedRawDecoder(IRawDecoder inner, ISettingsService settings, TimeSpan? retention = null)
    {
        _inner = inner;
        _settings = settings;
        _retention = retention ?? DefaultRetention;
    }

    public bool IsAvailable => _inner.IsAvailable;

    /// <summary>
    /// The file as it is on disk and the settings it is developed with: a file changed underneath, or
    /// a slider moved, is a different develop.
    /// </summary>
    private sealed record DevelopKey(string Path, long Size, long ModifiedTicks, RawDevelopSettings Develop);

    /// <summary>One develop in progress and how many callers are still waiting on it.</summary>
    private sealed class InFlight
    {
        public Task<DecodedImage?> Task { get; set; } = null!;

        public CancellationTokenSource Cancellation { get; } = new();

        public int Waiters { get; set; }
    }

    public async Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        var key = new DevelopKey(file.FullPath, file.SizeBytes, file.ModifiedUtc.UtcTicks, _settings.Current.RawDevelop);
        InFlight entry;

        lock (_gate)
        {
            if (_recent is { } recent && recent.Key == key)
            {
                return recent.Image;
            }

            if (!_inFlight.TryGetValue(key, out entry!))
            {
                // Its own cancellation, not the first caller's: one caller giving up must not take
                // the develop away from another still waiting on it.
                var started = new InFlight();
                started.Task = Task.Run(() => DevelopAndRemember(key, started, file));
                entry = started;
                _inFlight[key] = entry;
            }

            entry.Waiters++;
        }

        try
        {
            return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                // The last one out stops the work. Moving through a folder abandons develops one
                // after another, and each left running would hold a core for seconds.
                // Still registered means the develop has not reached its own clean-up, so its
                // cancellation source has not been disposed yet.
                if (--entry.Waiters == 0
                    && _inFlight.TryGetValue(key, out var current)
                    && ReferenceEquals(current, entry))
                {
                    entry.Cancellation.Cancel();
                    _inFlight.Remove(key);
                }
            }

            throw;
        }
    }

    private async Task<DecodedImage?> DevelopAndRemember(DevelopKey key, InFlight entry, MediaFile file)
    {
        DecodedImage? image = null;

        try
        {
            image = await _inner.DevelopAsync(file, entry.Cancellation.Token).ConfigureAwait(false);
            return image;
        }
        finally
        {
            lock (_gate)
            {
                // By identity: an abandoned develop finishing late must not unregister a newer one
                // for the same file that started after it was given up on.
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _inFlight.Remove(key);
                }

                if (image is not null && !entry.Cancellation.IsCancellationRequested)
                {
                    _recent = (key, image);
                }
            }

            entry.Cancellation.Dispose();

            if (image is not null)
            {
                _ = ForgetLaterAsync(image);
            }
        }
    }

    private async Task ForgetLaterAsync(DecodedImage image)
    {
        await Task.Delay(_retention).ConfigureAwait(false);

        lock (_gate)
        {
            // Only if nothing newer has taken the slot in the meantime.
            if (_recent is { } recent && ReferenceEquals(recent.Image, image))
            {
                _recent = null;
            }
        }
    }
}
