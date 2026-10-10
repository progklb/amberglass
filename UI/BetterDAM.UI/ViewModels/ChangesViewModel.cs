using System.Collections.ObjectModel;
using Avalonia.Threading;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

/// <summary>A field of a pending change, before and after.</summary>
public sealed record FieldRow(string Field, string? Was, string? Now)
{
    /// <summary>"—" for not present, so the reader can tell a cleared field from a blank one.</summary>
    public string WasText => Was ?? "—";

    public string NowText => Now ?? "—";
}

/// <summary>One keyword a pending change adds or takes away.</summary>
public sealed record KeywordRow(string Keyword, bool Added)
{
    public string Sign => Added ? "+" : "−";
}

/// <summary>A file with a pending change, as the Changes window lists it.</summary>
public sealed class ChangedFileViewModel
{
    public ChangedFileViewModel(PendingChange change, MediaFile file, MediaItemViewModel item, string folder)
    {
        Change = change;
        File = file;
        Item = item;
        Folder = folder;
        Diff = MetadataDiff.Between(change.Original, change.Edited);

        var rows = new List<object>(Diff.Fields.Length + Diff.KeywordsAdded.Length + Diff.KeywordsRemoved.Length);
        rows.AddRange(Diff.Fields.Select(f => new FieldRow(f.Field, f.Was, f.Now)));
        rows.AddRange(Diff.KeywordsAdded.Select(k => new KeywordRow(k, Added: true)));
        rows.AddRange(Diff.KeywordsRemoved.Select(k => new KeywordRow(k, Added: false)));
        Rows = rows;
    }

    public PendingChange Change { get; }

    public MediaFile File { get; }

    /// <summary>For the thumbnail, which the tile's own control knows how to fetch lazily.</summary>
    public MediaItemViewModel Item { get; }

    public string Path => Change.FilePath;

    public string Name => File.FileName;

    /// <summary>Where the file sits, relative to the workspace when it is inside one.</summary>
    public string Folder { get; }

    public MetadataDiff Diff { get; }

    public string Summary => Diff.Summary;

    /// <summary>Field rows first, then keywords added, then removed — one list, two templates.</summary>
    public IReadOnlyList<object> Rows { get; }

    public bool HasKeywordChanges => Diff.HasKeywordChanges;
}

/// <summary>A file whose embedded metadata and sidecar disagree, and on what.</summary>
public sealed class ConflictedFileViewModel
{
    public ConflictedFileViewModel(MediaFile file, MediaMetadata metadata, MediaItemViewModel item, string folder)
    {
        File = file;
        Metadata = metadata;
        Item = item;
        Folder = folder;
        Conflicts = MetadataConflictDetector.Detect(metadata);
    }

    public MediaFile File { get; }

    public MediaMetadata Metadata { get; }

    public MediaItemViewModel Item { get; }

    public string Path => File.FullPath;

    public string Name => File.FileName;

    public string Folder { get; }

    public IReadOnlyList<MetadataConflict> Conflicts { get; }

    public string Summary => string.Join(" · ", Conflicts.Select(c => c.Field.ToLowerInvariant()));
}

/// <summary>
/// The review of everything pending: what changed in each file, before and after, and the means
/// to write or discard any of it a file at a time.
///
/// The status bar's three buttons are all-or-nothing, and the pending store is held in memory —
/// quit, and every unsaved edit is gone. This is where a person finds out what that would cost,
/// and settles the twenty they are sure of without committing the two they are not.
///
/// Conflicts have a tab of their own because they are the same shape — two readings of one file,
/// disagreeing — and today surface only on a tile badge, one file at a time. Resolving one here
/// records the choice as a pending edit, as the inspector does; it moves to the Changes tab and
/// is written with everything else, not on its own.
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly IPendingChangeStore _pending;
    private readonly IThumbnailService _thumbnails;
    private readonly IMetadataWriter _writer;
    private readonly IMetadataProvider _metadata;
    private readonly ILogger<ChangesViewModel> _logger;

    private CancellationTokenSource? _checking;

    public ChangesViewModel(
        IPendingChangeStore pending,
        IThumbnailService thumbnails,
        IMetadataWriter writer,
        IMetadataProvider metadata,
        ILogger<ChangesViewModel> logger)
    {
        _pending = pending;
        _thumbnails = thumbnails;
        _writer = writer;
        _metadata = metadata;
        _logger = logger;

        _pending.Changed += OnPendingChanged;
        SelectedFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>The open workspace, so paths can be shown relative to it. Set by the caller.</summary>
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// The files the grid is currently showing. The conflict check runs over these — every file
    /// would mean reading the metadata of the whole workspace, which is a scan, not a check.
    /// Also where a pending file's <see cref="MediaFile"/> is found without touching the disk.
    /// </summary>
    public IReadOnlyList<MediaFile> Scope { get; set; } = [];

    /// <summary>Files whose sidecars were written here, so the caller can re-index them.</summary>
    public List<MediaFile> Written { get; } = [];

    /// <summary>
    /// True when the review was opened because the application is closing with edits unsaved.
    /// The window then says so, and offers the one thing a close guard has to: a way to quit anyway.
    /// </summary>
    public bool IsQuitPrompt { get; set; }

    /// <summary>
    /// A file to open straight onto, when the review was reached from that file's conflict warning
    /// rather than from the pending count. Set by the caller before the window is shown.
    /// </summary>
    public MediaFile? FocusConflict { get; set; }

    /// <summary>Which tab the window opens on. 0 is Changes, 1 is Conflicts.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>
    /// Opens on the one file that was asked about, without the scan.
    ///
    /// The conflict check reads every file in scope, which on a large folder is a minute. Arriving
    /// from one file's warning is a question about that file, so it is answered by reading that
    /// file — and the scan is still there for the broader question.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        if (FocusConflict is not { } file)
        {
            return;
        }

        SelectedTab = ConflictsTab;

        try
        {
            if (await _metadata.ReadAsync(file, cancellationToken).ConfigureAwait(true) is not { } metadata)
            {
                return;
            }

            if (MetadataConflictDetector.Detect(metadata).Length == 0)
            {
                // Settled between opening the warning and getting here.
                CheckStatus = "This file's metadata and its sidecar now agree.";
                HasChecked = true;
                return;
            }

            Conflicts.Clear();
            Conflicts.Add(new ConflictedFileViewModel(
                file, metadata, new MediaItemViewModel(file, _thumbnails), FolderOf(file)));

            SelectedConflict = Conflicts[0];
            HasChecked = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {File} for its conflicts", file.FullPath);
            CheckStatus = ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(ConflictHeading));
        }
    }

    /// <summary>The index of the Conflicts tab, named rather than written as a bare 1 at call sites.</summary>
    public const int ConflictsTab = 1;

    public string QuitBanner => Files.Count == 1
        ? "You are quitting with 1 unsaved change. Pending edits are kept in memory and will be lost."
        : $"You are quitting with {Files.Count:N0} unsaved changes. Pending edits are kept in memory and will be lost.";

    // ---- Changes --------------------------------------------------------------------------------

    public ObservableCollection<ChangedFileViewModel> Files { get; } = [];

    /// <summary>Bound to the list's own selection, so a multi-selection reaches the commands.</summary>
    public ObservableCollection<ChangedFileViewModel> SelectedFiles { get; } = [];

    [ObservableProperty]
    private ChangedFileViewModel? _selectedFile;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isWriting;

    public bool HasChanges => Files.Count > 0;

    public string Heading => Files.Count switch
    {
        0 => "Nothing pending",
        1 => "1 file with pending changes",
        var n => $"{n:N0} files with pending changes"
    };

    public bool CanWrite => _writer.IsAvailable;

    /// <summary>What the two buttons will act on: the selection, or the one file being viewed.</summary>
    public string SelectionSummary => SelectedFiles.Count switch
    {
        0 => string.Empty,
        1 => "1 file selected",
        var n => $"{n} files selected"
    };

    /// <summary>
    /// Rebuilds the list from the store, keeping whatever was selected where it still exists.
    /// Sorted by path so files from one folder sit together — the list is scanned, not searched.
    /// </summary>
    public void Load()
    {
        var keep = SelectedFile?.Path;
        var byPath = Scope.ToDictionary(f => f.FullPath, StringComparer.Ordinal);

        Files.Clear();

        foreach (var change in _pending.GetAll().OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!byPath.TryGetValue(change.FilePath, out var file))
            {
                // Edited in a folder that is no longer on screen. Described from disk, and
                // skipped only if it has gone — a change to a file that no longer exists cannot be
                // written and would only mislead.
                var info = new FileInfo(change.FilePath);
                if (!info.Exists)
                {
                    continue;
                }

                file = MediaFile.FromFileInfo(info);
            }

            Files.Add(new ChangedFileViewModel(change, file, new MediaItemViewModel(file, _thumbnails), FolderOf(file)));
        }

        SelectedFile = Files.FirstOrDefault(f => f.Path == keep) ?? Files.FirstOrDefault();

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(QuitBanner));
    }

    /// <summary>The files a command acts on: the selection if there is one, else the file in view.</summary>
    private IReadOnlyList<ChangedFileViewModel> Targets()
        => SelectedFiles.Count > 0 ? SelectedFiles.ToList()
            : SelectedFile is { } one ? [one]
            : [];

    /// <summary>
    /// Forgets the edits. The files are untouched — nothing was ever written — so this needs no
    /// confirmation beyond being a button that says what it does.
    /// </summary>
    [RelayCommand]
    private void DiscardSelected()
    {
        var targets = Targets();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            _pending.Discard(target.Path);
        }

        Status = targets.Count == 1
            ? $"Discarded the changes to {targets[0].Name}."
            : $"Discarded the changes to {targets.Count} files.";
    }

    /// <summary>
    /// Writes the selection's sidecars, and only sidecars — the originals are never touched from
    /// here. Each success leaves the store as it goes, so a failure part way leaves exactly the
    /// unwritten files pending rather than all or none.
    /// </summary>
    [RelayCommand]
    private async Task WriteSelectedAsync()
    {
        var targets = Targets();
        if (targets.Count == 0 || !CanWrite || IsWriting)
        {
            return;
        }

        IsWriting = true;
        var written = 0;
        var failed = 0;

        try
        {
            foreach (var target in targets)
            {
                Status = $"Writing {target.Name} — {written + failed + 1} of {targets.Count}";

                var result = await _writer
                    .WriteSidecarAsync(target.File, target.Change.Edited, new SidecarWriteOptions())
                    .ConfigureAwait(true);

                if (result.Success)
                {
                    _pending.Discard(target.Path);
                    Written.Add(target.File);
                    written++;
                }
                else
                {
                    _logger.LogWarning("Sidecar write failed for {File}: {Error}", target.Path, result.Error);
                    failed++;
                }
            }

            Status = failed == 0
                ? $"Wrote {written} sidecar{(written == 1 ? "" : "s")}. Original media untouched."
                : $"Wrote {written}, {failed} failed — see the log for details.";
        }
        finally
        {
            IsWriting = false;
        }
    }

    private void OnPendingChanged(object? sender, PendingChangesChangedEventArgs e)
        => Dispatcher.UIThread.Post(Load);

    // ---- Conflicts ------------------------------------------------------------------------------

    public ObservableCollection<ConflictedFileViewModel> Conflicts { get; } = [];

    [ObservableProperty]
    private ConflictedFileViewModel? _selectedConflict;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
    private string? _checkStatus;

    [ObservableProperty]
    private int _checkProgress;

    public int ScopeCount => Scope.Count;

    public string CheckLabel => Scope.Count switch
    {
        0 => "Nothing to check",
        1 => "Check 1 file",
        var n => $"Check {n:N0} files"
    };

    public bool HasConflicts => Conflicts.Count > 0;

    public string ConflictHeading => !HasChecked
        ? "Not checked yet"
        : Conflicts.Count switch
        {
            0 => "No conflicts",
            1 => "1 file in conflict",
            var n => $"{n:N0} files in conflict"
        };

    /// <summary>
    /// Reads every file in scope and keeps the ones whose two layers disagree. An explicit action
    /// with progress and a cancel, because on a large folder it is a minute of ExifTool, and a
    /// minute nobody asked for is a hang.
    /// </summary>
    [RelayCommand]
    private async Task CheckConflictsAsync()
    {
        if (IsChecking || Scope.Count == 0)
        {
            return;
        }

        _checking?.Dispose();
        var cts = new CancellationTokenSource();
        _checking = cts;

        IsChecking = true;
        CheckProgress = 0;
        CheckStatus = $"Reading 0 of {Scope.Count:N0}";

        try
        {
            var progress = new Progress<int>(done =>
            {
                CheckProgress = done;
                CheckStatus = $"Reading {done:N0} of {Scope.Count:N0}";
            });

            var read = await _metadata.ReadManyAsync(Scope, progress, cts.Token).ConfigureAwait(true);

            Conflicts.Clear();
            foreach (var file in Scope)
            {
                if (read.TryGetValue(file.FullPath, out var metadata)
                    && MetadataConflictDetector.Detect(metadata).Length > 0)
                {
                    Conflicts.Add(new ConflictedFileViewModel(file, metadata, new MediaItemViewModel(file, _thumbnails), FolderOf(file)));
                }
            }

            SelectedConflict = Conflicts.FirstOrDefault();
            HasChecked = true;
            CheckStatus = null;
        }
        catch (OperationCanceledException)
        {
            CheckStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conflict check failed");
            CheckStatus = ex.Message;
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(ConflictHeading));
        }
    }

    [RelayCommand]
    private void CancelCheck() => _checking?.Cancel();

    /// <summary>
    /// Settles a file's conflicts by recording the chosen side as a pending edit — a decision,
    /// not a write. The file leaves this tab and joins the changes, exactly as it would have from
    /// the inspector.
    /// </summary>
    [RelayCommand]
    private void Resolve(string? resolution)
    {
        if (SelectedConflict is not { } conflicted
            || !Enum.TryParse<ConflictResolution>(resolution, out var choice))
        {
            return;
        }

        var resolved = MetadataConflictDetector.Resolve(conflicted.Metadata, choice);
        _pending.Set(conflicted.Path, conflicted.Metadata.Effective, resolved);

        var index = Conflicts.IndexOf(conflicted);
        Conflicts.Remove(conflicted);
        SelectedConflict = Conflicts.Count == 0 ? null : Conflicts[Math.Min(index, Conflicts.Count - 1)];

        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictHeading));
    }

    // ---- Shared ---------------------------------------------------------------------------------

    private string FolderOf(MediaFile file)
    {
        var folder = System.IO.Path.GetDirectoryName(file.FullPath) ?? string.Empty;

        if (WorkspacePath is { } root && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var relative = folder[root.Length..].TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return relative.Length == 0 ? "workspace" : relative;
        }

        return folder;
    }

    /// <summary>Unhooks the store; the window is transient and the store is not.</summary>
    public void Detach()
    {
        _pending.Changed -= OnPendingChanged;
        _checking?.Cancel();
    }
}
