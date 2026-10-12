using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// The Prepare Workspace dialog: says what the work is, roughly what it costs, and then does it.
///
/// The costing is the point of the dialog. Preparing a few hundred photographs is a coffee break;
/// preparing a few thousand RAWs is most of an afternoon and tens of gigabytes, and nobody should
/// discover which one they started by watching it run.
/// </summary>
public sealed partial class PrepareWorkspaceViewModel : ObservableObject
{
    private readonly IWorkspacePreparer _preparer;
    private readonly IVideoProxyService _proxies;
    private readonly ISettingsService _settings;
    private readonly ILogger<PrepareWorkspaceViewModel> _logger;

    private CancellationTokenSource? _cts;

    public PrepareWorkspaceViewModel(
        IWorkspacePreparer preparer,
        IVideoProxyService proxies,
        ISettingsService settings,
        ILogger<PrepareWorkspaceViewModel> logger)
    {
        _preparer = preparer;
        _proxies = proxies;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Set by the caller before the dialog is shown.</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    public string WorkspaceName => WorkspaceLabel.ForMenu(WorkspacePath);

    public static IReadOnlyList<string> ProxyQualities { get; } = ["360p", "480p", "720p"];

    private static readonly VideoQuality[] QualityValues = [VideoQuality.P360, VideoQuality.P480, VideoQuality.P720];

    [ObservableProperty]
    private int _selectedQualityIndex = 2;

    /// <summary>
    /// Off by default. Photographs are the reason to prepare a workspace; video proxies are an order
    /// of magnitude more work and only worth it if the clips are actually going to be watched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateSummary))]
    private bool _includeVideoProxies;

    public bool CanIncludeVideo => _proxies.IsAvailable && Estimate.Videos > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateSummary))]
    [NotifyPropertyChangedFor(nameof(ContentsSummary))]
    [NotifyPropertyChangedFor(nameof(CanIncludeVideo))]
    [NotifyPropertyChangedFor(nameof(HasRenderCacheWarning))]
    private WorkspaceEstimate _estimate = WorkspaceEstimate.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isEstimating = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isFinished;

    public bool IsReady => !IsEstimating && !IsRunning;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string? _resultText;

    /// <summary>
    /// Developing every RAW only pays off if the results are kept. Without the render cache the work
    /// still warms the thumbnails, but the expensive half is thrown away as soon as it is produced.
    /// </summary>
    public bool HasRenderCacheWarning => Estimate.RawImages > 0 && !_settings.Current.RenderCacheEnabled;

    public string ContentsSummary => Estimate.IsEmpty
        ? "Nothing to prepare."
        : string.Join("  ·  ", Parts());

    private IEnumerable<string> Parts()
    {
        if (Estimate.RawImages > 0)
        {
            yield return $"{Estimate.RawImages:N0} RAW";
        }

        var other = Estimate.Images - Estimate.RawImages;
        if (other > 0)
        {
            yield return $"{other:N0} other image{(other == 1 ? "" : "s")}";
        }

        if (Estimate.Videos > 0)
        {
            yield return $"{Estimate.Videos:N0} video{(Estimate.Videos == 1 ? "" : "s")}";
        }
    }

    /// <summary>
    /// Disk and time. Approximate, and said to be — the figures come from measured averages, and a
    /// library that is all panoramas or all small JPEGs will not match them.
    /// </summary>
    public string EstimateSummary
    {
        get
        {
            if (Estimate.IsEmpty)
            {
                return string.Empty;
            }

            var bytes = Estimate.ImageBytes;
            var time = Estimate.EstimateImageTime(_preparer.Parallelism);
            var summary = $"About {ByteSize.Format(bytes)} and roughly {Describe(time)}, {_preparer.Parallelism} at a time.";

            if (!IncludeVideoProxies || Estimate.Videos == 0)
            {
                return summary;
            }

            // Video is given as a size only. Encoding time depends on running time, and finding that
            // out means probing every file — which would make the dialog slow to answer a question
            // it can only answer roughly anyway.
            return summary +
                   $" Video proxies add roughly {ByteSize.Format(Estimate.ProxyBytes)} and can take considerably longer;" +
                   " they run after the photographs.";
        }
    }

    private static string Describe(TimeSpan time) => time switch
    {
        { TotalMinutes: < 1 } => "under a minute",
        { TotalMinutes: < 90 } => $"{Math.Round(time.TotalMinutes)} minutes",
        _ => $"{time.TotalHours:F1} hours"
    };

    public async Task EstimateAsync()
    {
        IsEstimating = true;

        try
        {
            Estimate = await _preparer.EstimateAsync(WorkspacePath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not size up {Workspace}", WorkspacePath);
            ResultText = "Could not read the workspace.";
        }
        finally
        {
            IsEstimating = false;
            OnPropertyChanged(nameof(CanIncludeVideo));
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ResultText = null;
        ProgressFraction = 0;
        ProgressText = "Starting…";

        var progress = new Progress<PreparationProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressText = $"{p.Stage} — {p.Completed:N0} of {p.Total:N0}  ·  {p.CurrentFile}";
        });

        try
        {
            var options = new PreparationOptions(
                IncludeVideoProxies && CanIncludeVideo,
                QualityValues[Math.Clamp(SelectedQualityIndex, 0, QualityValues.Length - 1)]);

            var result = await _preparer
                .PrepareAsync(WorkspacePath, options, progress, _cts.Token)
                .ConfigureAwait(true);

            ResultText = Describe(result);
            IsFinished = !result.Cancelled;
        }
        catch (OperationCanceledException)
        {
            ResultText = "Stopped. What was already prepared has been kept.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing {Workspace} failed", WorkspacePath);
            ResultText = $"Preparation failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string Describe(PreparationResult result)
    {
        var parts = new List<string> { $"{result.Prepared:N0} prepared" };

        if (result.Skipped > 0)
        {
            parts.Add($"{result.Skipped:N0} already done");
        }

        if (result.Failed > 0)
        {
            parts.Add($"{result.Failed:N0} could not be read");
        }

        return (result.Cancelled ? "Stopped. " : "Finished. ") + string.Join(", ", parts) + ".";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
