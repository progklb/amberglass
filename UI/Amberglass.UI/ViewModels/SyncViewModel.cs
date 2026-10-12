using System.Collections.ObjectModel;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

public sealed partial class SyncViewModel : ObservableObject
{
    private readonly ISyncService _sync;
    private readonly ILogger<SyncViewModel> _logger;

    private SyncPlan _plan = SyncPlan.Empty;
    private CancellationTokenSource? _cts;

    public SyncViewModel(ISyncService sync, ILogger<SyncViewModel> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    public ObservableCollection<string> Breakdown { get; } = [];

    public ObservableCollection<SyncItemResult> Failures { get; } = [];

    [ObservableProperty]
    private bool _isPreparing = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasFinished;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private bool _isResuming;

    [ObservableProperty]
    private int _alreadyCompletedCount;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string? _progressLabel;

    [ObservableProperty]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _resultIsFailure;

    // Options. Embedding is off by default: it is the only thing here that touches originals.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionSummary))]
    private bool _embedMetadata;

    [ObservableProperty]
    private bool _backupOriginals = true;

    [ObservableProperty]
    private bool _preserveTimestamps = true;

    [ObservableProperty]
    private bool _validateAfterWriting = true;

    [ObservableProperty]
    private bool _skipConflicted = true;

    public bool HasWork => FileCount > 0;

    public bool HasConflicts => ConflictCount > 0;

    /// <summary>Says in plain words what pressing the button will do to the user's files.</summary>
    public string ActionSummary => EmbedMetadata
        ? "XMP sidecars will be written, and metadata will be written into the original media files."
        : "XMP sidecars will be written. Your original media will not be modified.";

    public SyncOptions BuildOptions() => new()
    {
        EmbedMetadata = EmbedMetadata,
        BackupOriginals = BackupOriginals,
        PreserveTimestamps = PreserveTimestamps,
        ValidateAfterWriting = ValidateAfterWriting,
        SkipConflicted = SkipConflicted
    };

    public async Task PrepareAsync()
    {
        IsPreparing = true;

        try
        {
            _plan = await _sync.PrepareAsync(BuildOptions()).ConfigureAwait(true);

            FileCount = _plan.Count;
            ConflictCount = _plan.ConflictCount;
            IsResuming = _plan.IsResuming;
            AlreadyCompletedCount = _plan.AlreadyCompleted.Count;

            Breakdown.Clear();
            foreach (var (extension, count) in _plan.ByExtension)
            {
                Breakdown.Add($"{count:N0} {extension}");
            }

            OnPropertyChanged(nameof(HasWork));
            OnPropertyChanged(nameof(HasConflicts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not prepare the sync plan");
            ResultIsFailure = true;
            ResultMessage = ex.Message;
        }
        finally
        {
            IsPreparing = false;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_plan.Count == 0 || IsRunning)
        {
            return;
        }

        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsRunning = true;
        HasFinished = false;
        Failures.Clear();
        ResultMessage = null;
        ResultIsFailure = false;
        ProgressFraction = 0;

        try
        {
            var progress = new Progress<JobProgress>(p =>
            {
                ProgressFraction = p.Fraction;
                ProgressLabel = $"{p.Completed:N0} of {p.Total:N0} — {p.CurrentItem}";
            });

            var result = await _sync.ExecuteAsync(_plan, BuildOptions(), progress, cts.Token).ConfigureAwait(true);

            foreach (var failure in result.Failures)
            {
                Failures.Add(failure);
            }

            ResultIsFailure = result.Failures.Count > 0;
            ResultMessage = Describe(result);
            HasFinished = true;

            // Re-plan so the summary reflects what is actually left. Without this the dialog still
            // claims "9 file(s) pending" directly above "9 file(s) written", which reads as a
            // failure even though everything succeeded.
            var message = ResultMessage;
            var wasFailure = ResultIsFailure;
            await PrepareAsync().ConfigureAwait(true);
            ResultMessage = message;
            ResultIsFailure = wasFailure;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            ResultIsFailure = true;
            ResultMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
            ProgressLabel = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Re-plans after a run so a retry only covers what is still outstanding.</summary>
    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        HasFinished = false;
        await PrepareAsync().ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DiscardResumeStateAsync()
    {
        _sync.DiscardResumeState();
        await PrepareAsync().ConfigureAwait(true);
    }

    private string Describe(SyncResult result)
    {
        var parts = new List<string>();

        if (result.Succeeded > 0)
        {
            parts.Add(EmbedMetadata
                ? $"{result.Succeeded:N0} file(s) written and embedded"
                : $"{result.Succeeded:N0} sidecar(s) written");
        }

        if (result.Skipped > 0)
        {
            parts.Add($"{result.Skipped:N0} skipped");
        }

        if (result.Failures.Count > 0)
        {
            parts.Add($"{result.Failures.Count:N0} failed");
        }

        if (result.WasCancelled)
        {
            parts.Add("cancelled — the finished files are recorded, so syncing again resumes");
        }

        return parts.Count == 0 ? "Nothing to do." : string.Join(", ", parts) + ".";
    }
}
