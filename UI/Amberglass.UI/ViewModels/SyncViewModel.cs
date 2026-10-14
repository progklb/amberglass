using System.Collections.ObjectModel;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One file in the Sync window: what it will say afterwards, and where Sync will write it.
///
/// Laid out like a file in the Changes review on purpose, so the two windows read the same way.
/// The difference is the action, which depends on the options and is refreshed as they change.
/// </summary>
public sealed partial class SyncFileViewModel : ObservableObject
{
    public SyncFileViewModel(SyncPlanItem plan, MediaItemViewModel item, string folder, SyncOptions options)
    {
        Plan = plan;
        Item = item;
        Folder = folder;
        Diff = MetadataDiff.Between(plan.Before, plan.Edited);
        Rows = DiffRows.From(Diff);
        Refresh(options);
    }

    public SyncPlanItem Plan { get; }

    /// <summary>For the thumbnail, which the tile's own control knows how to fetch lazily.</summary>
    public MediaItemViewModel Item { get; }

    public string Name => Plan.File.FileName;

    public string Folder { get; }

    public MetadataDiff Diff { get; }

    public IReadOnlyList<object> Rows { get; }

    public string Summary => Diff.IsEmpty ? "no field changes" : Diff.Summary;

    public bool HasConflict => Plan.HasConflict;

    /// <summary>
    /// What the left column holds. For an edit it is where the edit started; for a file queued only
    /// to be embedded it is the file itself, and saying so is the point of the comparison.
    /// </summary>
    public string BeforeHeading => Plan.HasEdit ? "BEFORE" : "IN THE FILE NOW";

    /// <summary>Taking the file out of the run means different things for the two kinds of work.</summary>
    public string DropLabel => Plan.HasEdit ? "Discard changes" : "Don't embed";

    public string DropTip => Plan.HasEdit
        ? "Forget this file's unsaved edits. Nothing on disk changes — nothing was written yet."
        : "Take this file off the embed queue. Its sidecar is unaffected.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(ActionDescription))]
    private SyncAction _action;

    /// <summary>The short tag on the list row.</summary>
    public string ActionLabel => Action switch
    {
        SyncAction.SidecarAndFile => "Sidecar + file",
        SyncAction.FileOnly => "File only",
        _ => "Sidecar only"
    };

    /// <summary>The same, in a sentence, above the comparison.</summary>
    public string ActionDescription => Action switch
    {
        SyncAction.SidecarAndFile => "Writes the XMP sidecar, and the same values into the file itself.",
        SyncAction.FileOnly => "Writes the sidecar's values into the file itself. The sidecar already has them and is left as it is.",
        _ when !MediaTypeRegistry.CanEmbedMetadata(Plan.File.FullPath)
            => "Writes the XMP sidecar. This is a camera raw file, so the sidecar is its record and the file itself is never written to.",
        _ => "Writes the XMP sidecar. The file itself is not modified."
    };

    public void Refresh(SyncOptions options) => Action = Plan.ActionFor(options);
}

public sealed partial class SyncViewModel : ObservableObject
{
    private readonly ISyncService _sync;
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<SyncViewModel> _logger;

    private SyncPlan _plan = SyncPlan.Empty;
    private CancellationTokenSource? _cts;

    private readonly IPendingChangeStore _pending;
    private readonly IEmbedQueue _embedQueue;

    public SyncViewModel(
        ISyncService sync,
        IThumbnailService thumbnails,
        IPendingChangeStore pending,
        IEmbedQueue embedQueue,
        ILogger<SyncViewModel> logger)
    {
        _sync = sync;
        _thumbnails = thumbnails;
        _pending = pending;
        _embedQueue = embedQueue;
        _logger = logger;
    }

    /// <summary>
    /// A file the user wants to see in the grid, set when they ask to. The main window selects it
    /// once this window has closed — the window is modal, so the inspector cannot be used until then.
    /// </summary>
    public string? RevealPath { get; private set; }

    /// <summary>Raised when the window should close on the view model's behalf.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Closes Sync and selects the file in the main window, so it can be changed in the inspector.
    /// Nothing is lost: the plan is rebuilt the next time Sync opens.
    /// </summary>
    [RelayCommand]
    private void ShowInGrid(SyncFileViewModel? file)
    {
        if (file is null || IsRunning)
        {
            return;
        }

        RevealPath = file.Plan.File.FullPath;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Takes one file out of the run: an edit is discarded, as the review's Discard does, and a file
    /// queued only to be embedded leaves the queue. No confirmation, for the review's reason — nothing
    /// on disk changes, and the button says what it does.
    /// </summary>
    [RelayCommand]
    private void DropFile(SyncFileViewModel? file)
    {
        if (file is null || IsRunning)
        {
            return;
        }

        var path = file.Plan.File.FullPath;
        if (file.Plan.HasEdit)
        {
            _pending.Discard(path);
        }

        _embedQueue.Remove(path);

        var index = Files.IndexOf(file);
        _plan = _plan with { Items = _plan.Items.Where(i => i.File.FullPath != path).ToList() };
        ShowPlan();
        SelectedFile = Files.Count == 0 ? null : Files[Math.Clamp(index, 0, Files.Count - 1)];
    }

    /// <summary>The open workspace, so paths can be shown relative to it. Set by the caller.</summary>
    public string? WorkspacePath { get; set; }

    /// <summary>Every file the run will touch, sorted by path so a folder's files sit together.</summary>
    public ObservableCollection<SyncFileViewModel> Files { get; } = [];

    [ObservableProperty]
    private SyncFileViewModel? _selectedFile;

    /// <summary>"12 files · 9 JPG · 3 RAF", the header over the list.</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

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
    private bool _embedMetadata;

    /// <summary>Each file's action follows the option, and the summary follows the files.</summary>
    partial void OnEmbedMetadataChanged(bool value) => RefreshActions();

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

    /// <summary>
    /// Says in plain words what pressing the button will do to the user's files, counted from the
    /// files themselves: with queued embeds, "your originals will not be modified" can be untrue
    /// even with the embed option off.
    /// </summary>
    [ObservableProperty]
    private string _actionSummary = string.Empty;

    private void RefreshActions()
    {
        var options = BuildOptions();
        foreach (var file in Files)
        {
            file.Refresh(options);
        }

        var sidecars = Files.Count(f => f.Action != SyncAction.FileOnly);
        var embeds = Files.Count(f => f.Action != SyncAction.SidecarOnly);

        var parts = new List<string>(2);
        if (sidecars > 0)
        {
            parts.Add(sidecars == 1 ? "1 XMP sidecar will be written" : $"{sidecars:N0} XMP sidecars will be written");
        }

        if (embeds > 0)
        {
            parts.Add(embeds == 1 ? "1 original file will have metadata written into it" : $"{embeds:N0} original files will have metadata written into them");
        }

        ActionSummary = parts.Count == 0 ? "Nothing to do."
            : embeds == 0 ? parts[0] + ". Your original media will not be modified."
            : string.Join(", and ", parts) + ".";
    }

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
            ShowPlan();
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

    /// <summary>Lays the plan out: the list, the counts and the heading over it.</summary>
    private void ShowPlan()
    {
        FileCount = _plan.Count;
        ConflictCount = _plan.ConflictCount;
        IsResuming = _plan.IsResuming;
        AlreadyCompletedCount = _plan.AlreadyCompleted.Count;

        var keep = SelectedFile?.Plan.File.FullPath;
        var options = BuildOptions();
        Files.Clear();
        foreach (var item in _plan.Items)
        {
            Files.Add(new SyncFileViewModel(
                item,
                new MediaItemViewModel(item.File, _thumbnails),
                ChangesViewModel.FolderOf(item.File, WorkspacePath),
                options));
        }

        SelectedFile = Files.FirstOrDefault(f => f.Plan.File.FullPath == keep) ?? Files.FirstOrDefault();

        var types = _plan.ByExtension.Select(e => $"{e.Count:N0} {e.Extension}");
        Heading = FileCount == 0
            ? "Nothing to sync"
            : string.Join(" · ", [FileCount == 1 ? "1 file" : $"{FileCount:N0} files", .. types]);

        RefreshActions();

        OnPropertyChanged(nameof(HasWork));
        OnPropertyChanged(nameof(HasConflicts));
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

        var sidecarsOnly = result.Succeeded - result.Embedded;
        if (sidecarsOnly > 0)
        {
            parts.Add($"{sidecarsOnly:N0} sidecar(s) written");
        }

        if (result.Embedded > 0)
        {
            parts.Add($"{result.Embedded:N0} file(s) embedded");
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
