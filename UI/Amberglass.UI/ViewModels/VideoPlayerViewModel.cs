using System.Diagnostics;
using Avalonia.Threading;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// Drives video preview: proxy selection, transport, scrubbing and frame stepping.
///
/// Video frames are decoded and paced against a wall clock; audio is decoded separately from the
/// same file and played through the platform sound device. The two are started together at the same
/// position and each runs at its natural rate, which is accurate enough for preview-length clips
/// without video having to be slaved to an audio clock.
/// </summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private readonly IVideoProxyService _proxies;
    private readonly IVideoFrameSource _frames;
    private readonly IVideoInfoProvider _infoProvider;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly IAudioPlayer _audio;
    private readonly ILogger<VideoPlayerViewModel> _logger;

    private CancellationTokenSource? _playbackCts;
    private CancellationTokenSource? _loadCts;
    private MediaFile? _file;
    private VideoProxy? _proxy;

    /// <summary>Guards the slider binding while playback moves the position itself.</summary>
    private bool _suppressSeek;

    public VideoPlayerViewModel(
        IVideoProxyService proxies,
        IVideoFrameSource frames,
        IVideoInfoProvider infoProvider,
        IFfmpegLocator ffmpeg,
        IAudioPlayer audio,
        ILogger<VideoPlayerViewModel> logger)
    {
        _proxies = proxies;
        _frames = frames;
        _infoProvider = infoProvider;
        _ffmpeg = ffmpeg;
        _audio = audio;
        _logger = logger;

        _audio.Volume = EffectiveVolume;
    }

    /// <summary>Raised on the UI thread with a frame to display; the frame is disposed afterwards.</summary>
    public event Action<VideoFrame>? FrameReady;

    /// <summary>Raised when the surface should go blank.</summary>
    public event Action? SurfaceCleared;

    public static IReadOnlyList<string> QualityChoices { get; } = ["Original", "720p", "480p", "360p"];

    private static readonly VideoQuality[] Qualities =
        [VideoQuality.Original, VideoQuality.P720, VideoQuality.P480, VideoQuality.P360];

    [ObservableProperty]
    private bool _hasVideo;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionDisplay))]
    private double _positionSeconds;

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    [ObservableProperty]
    private int _selectedQualityIndex;

    [ObservableProperty]
    private bool _isPreparing;

    [ObservableProperty]
    private double _proxyProgress;

    [ObservableProperty]
    private string? _statusMessage;

    public string PositionDisplay => Format(TimeSpan.FromSeconds(PositionSeconds));

    public bool IsAvailable => _ffmpeg.IsAvailable;

    /// <summary>
    /// 0 to 1. Applied to the samples as they pass through, so a change is heard within the length
    /// of one decode chunk rather than at the next seek.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveVolume))]
    [NotifyPropertyChangedFor(nameof(VolumeDisplay))]
    private double _volume = 0.8;

    /// <summary>
    /// Separate from <see cref="Volume"/> so unmuting returns to the level that was set rather than
    /// to a default.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveVolume))]
    [NotifyPropertyChangedFor(nameof(VolumeDisplay))]
    private bool _isMuted;

    public double EffectiveVolume => IsMuted ? 0 : Volume;

    public string VolumeDisplay => IsMuted ? "Muted" : $"{EffectiveVolume * 100:F0}%";

    /// <summary>
    /// False for a silent file, or where the platform has no audio backend. Drives the control's
    /// enabled state so a dead volume slider is never offered.
    /// </summary>
    public bool CanPlayAudio => _audio.IsAvailable && _proxy?.Info.HasAudio == true;

    partial void OnVolumeChanged(double value) => _audio.Volume = EffectiveVolume;

    partial void OnIsMutedChanged(bool value) => _audio.Volume = EffectiveVolume;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    private VideoQuality SelectedQuality => Qualities[Math.Clamp(SelectedQualityIndex, 0, Qualities.Length - 1)];

    partial void OnPositionSecondsChanged(double value)
    {
        if (_suppressSeek)
        {
            return;
        }

        // The user dragged the slider.
        _ = SeekAsync(TimeSpan.FromSeconds(value));
    }

    partial void OnSelectedQualityIndexChanged(int value) => _ = ReloadForQualityAsync();

    public async Task LoadAsync(MediaFile? file)
    {
        await StopPlaybackAsync().ConfigureAwait(true);

        if (_loadCts is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        _file = file;
        _proxy = null;
        OnPropertyChanged(nameof(CanPlayAudio));
        StatusMessage = null;
        ProxyProgress = 0;

        if (file is null || file.MediaType != MediaType.Video || !_ffmpeg.IsAvailable)
        {
            HasVideo = false;
            SurfaceCleared?.Invoke();
            _loadCts = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;

        HasVideo = true;
        SetPosition(TimeSpan.Zero);

        try
        {
            var info = await _infoProvider.GetInfoAsync(file, cts.Token).ConfigureAwait(true);
            if (info is null || !info.IsUsable)
            {
                StatusMessage = "This file could not be opened for playback.";
                HasVideo = false;
                return;
            }

            DurationSeconds = info.Duration.TotalSeconds;
            DurationDisplay = Format(info.Duration);

            await PrepareAsync(cts.Token).ConfigureAwait(true);
            await ShowFrameAtAsync(TimeSpan.Zero, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {File} for playback", file.FullPath);
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Resolves the proxy for the selected quality, generating it if necessary.</summary>
    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_file is not { } file)
        {
            return;
        }

        var quality = SelectedQuality;
        var needsGeneration = quality != VideoQuality.Original && !_proxies.HasProxy(file, quality);

        if (needsGeneration)
        {
            IsPreparing = true;
            StatusMessage = $"Generating {QualityChoices[SelectedQualityIndex]} proxy…";
        }

        try
        {
            var progress = new Progress<double>(p => ProxyProgress = p);
            _proxy = await _proxies.GetProxyAsync(file, quality, progress, cancellationToken).ConfigureAwait(true);
            OnPropertyChanged(nameof(CanPlayAudio));

            StatusMessage = _proxy is null
                ? "Could not prepare this video for playback."
                : DescribeSource(_proxy);
        }
        finally
        {
            IsPreparing = false;
        }
    }

    private string DescribeSource(VideoProxy proxy)
    {
        var resolution = $"{proxy.Info.Width}×{proxy.Info.Height}";

        // Being explicit about what is on screen matters: a proxy is not the original pixels.
        return proxy.Quality == VideoQuality.Original
            ? $"Playing the original at {resolution}"
            : $"Playing a {resolution} proxy — the original is untouched";
    }

    private async Task ReloadForQualityAsync()
    {
        if (_file is null || !HasVideo)
        {
            return;
        }

        var wasPlaying = IsPlaying;
        var position = TimeSpan.FromSeconds(PositionSeconds);

        await StopPlaybackAsync().ConfigureAwait(true);

        var cts = _loadCts ?? new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            await PrepareAsync(cts.Token).ConfigureAwait(true);

            if (wasPlaying)
            {
                StartPlayback(position);
            }
            else
            {
                await ShowFrameAtAsync(position, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (!HasVideo || _proxy is null)
        {
            return;
        }

        if (IsPlaying)
        {
            await StopPlaybackAsync().ConfigureAwait(true);
            return;
        }

        var start = TimeSpan.FromSeconds(PositionSeconds);

        // Restart from the beginning when play is pressed at the end.
        if (start >= TimeSpan.FromSeconds(DurationSeconds - 0.05))
        {
            start = TimeSpan.Zero;
        }

        StartPlayback(start);
    }

    [RelayCommand]
    private Task StepBackAsync() => StepAsync(-1);

    [RelayCommand]
    private Task StepForwardAsync() => StepAsync(1);

    private async Task StepAsync(int frames)
    {
        if (_proxy is null)
        {
            return;
        }

        await StopPlaybackAsync().ConfigureAwait(true);

        var step = _proxy.Info.FrameDuration * frames;
        var target = TimeSpan.FromSeconds(PositionSeconds) + step;
        await SeekAsync(Clamp(target)).ConfigureAwait(true);
    }

    private async Task SeekAsync(TimeSpan position)
    {
        if (_proxy is null)
        {
            return;
        }

        var wasPlaying = IsPlaying;
        await StopPlaybackAsync().ConfigureAwait(true);

        SetPosition(Clamp(position));

        if (wasPlaying)
        {
            StartPlayback(TimeSpan.FromSeconds(PositionSeconds));
        }
        else
        {
            await ShowFrameAtAsync(TimeSpan.FromSeconds(PositionSeconds), CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task ShowFrameAtAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        if (_proxy is null)
        {
            return;
        }

        try
        {
            using var frame = await _frames
                .GetFrameAsync(_proxy.ProxyPath, _proxy.Info, position, cancellationToken)
                .ConfigureAwait(true);

            if (frame is not null)
            {
                FrameReady?.Invoke(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not show a frame at {Position}", position);
        }
    }

    /// <summary>
    /// Re-emits the frame at the current position, for a surface that has only just appeared —
    /// otherwise a view opened while paused stays black until playback is started.
    /// </summary>
    public Task RefreshFrameAsync()
        => IsPlaying || _proxy is null
            ? Task.CompletedTask
            : ShowFrameAtAsync(TimeSpan.FromSeconds(PositionSeconds), CancellationToken.None);

    private void StartPlayback(TimeSpan from)
    {
        if (_proxy is not { } proxy)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        IsPlaying = true;

        // Same file, same position: the proxy keeps its audio track, so nothing has to be
        // correlated between two different sources.
        if (CanPlayAudio)
        {
            _ = _audio.StartAsync(proxy.ProxyPath, from, cts.Token);
        }

        _ = Task.Run(() => PlaybackLoopAsync(proxy, from, cts.Token), cts.Token);
    }

    /// <summary>
    /// Decoding runs far ahead of realtime, so the loop paces itself: each frame carries a
    /// timestamp and is held until the wall clock catches up. That keeps playback at the right
    /// speed without a timer, and degrades to "as fast as it can" if decoding ever falls behind.
    /// </summary>
    private async Task PlaybackLoopAsync(VideoProxy proxy, TimeSpan from, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            await foreach (var frame in _frames.StreamAsync(proxy.ProxyPath, proxy.Info, from, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var due = frame.Position - from;
                var wait = due - clock.Elapsed;

                if (wait > TimeSpan.FromMilliseconds(1))
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    FrameReady?.Invoke(frame);
                    SetPosition(frame.Position);
                });

                frame.Dispose();
            }

            // Reaching the end stops playback rather than looping.
            await _audio.StopAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playback failed for {Path}", proxy.ProxyPath);
            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);
        }
    }

    private async Task StopPlaybackAsync()
    {
        IsPlaying = false;

        await _audio.StopAsync().ConfigureAwait(true);

        if (_playbackCts is not { } cts)
        {
            return;
        }

        _playbackCts = null;

        await cts.CancelAsync().ConfigureAwait(true);
        cts.Dispose();
    }

    /// <summary>Moves the position without it being mistaken for the user dragging the slider.</summary>
    private void SetPosition(TimeSpan position)
    {
        _suppressSeek = true;
        try
        {
            PositionSeconds = position.TotalSeconds;
        }
        finally
        {
            _suppressSeek = false;
        }
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        var max = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > max ? max : position;
    }

    private static string Format(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    public void Dispose()
    {
        _audio.Dispose();
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _loadCts?.Dispose();
    }
}
