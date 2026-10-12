using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// Batch metadata editing for a multi-file selection.
///
/// Deliberately an "apply these changes" form rather than a merged view of existing values: showing
/// common values would mean reading every selected file before the user has decided anything, and
/// every field is opt-in so a blank box can never mean "clear this on 500 files".
/// </summary>
public sealed partial class BatchEditViewModel : ObservableObject
{
    private readonly IBatchMetadataService _batch;
    private readonly IKeywordClipboard _clipboard;
    private readonly ILogger<BatchEditViewModel> _logger;

    private CancellationTokenSource? _jobCts;
    private IReadOnlyList<MediaItemViewModel> _items = [];

    public BatchEditViewModel(
        IBatchMetadataService batch,
        IKeywordClipboard clipboard,
        ILogger<BatchEditViewModel> logger)
    {
        _batch = batch;
        _clipboard = clipboard;
        _logger = logger;

        _clipboard.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanPasteKeywords));
            OnPropertyChanged(nameof(PasteKeywordsSummary));
            PasteKeywordsCommand.NotifyCanExecuteChanged();
        };
    }

    // ---- Pasting keywords -----------------------------------------------------------------------

    public bool CanPasteKeywords => _clipboard.HasKeywords && SelectedCount > 0 && !IsRunning;

    public string PasteKeywordsSummary => _clipboard.HasKeywords
        ? $"Paste {string.Join(", ", _clipboard.Keywords)}"
        : "Nothing copied";

    /// <summary>
    /// Adds the copied keywords to every selected file.
    ///
    /// Added rather than replacing. "Give these the same tags as that one" is what copying keywords
    /// means nearly always, and a paste that silently discarded what was already there would be the
    /// kind of mistake noticed a hundred files later. Replacing is still available in this panel, as
    /// a deliberate choice with its own checkbox.
    ///
    /// Goes through the same batch service as every other bulk edit, so pasted keywords are a pending
    /// change like any other and nothing reaches the files until Sync.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPasteKeywords))]
    private async Task PasteKeywordsAsync()
    {
        if (!_clipboard.HasKeywords || _items.Count == 0)
        {
            return;
        }

        var edit = new BatchMetadataEdit { KeywordsToAdd = [.. _clipboard.Keywords] };

        await RunAsync(edit, $"Pasted {_clipboard.Keywords.Length:N0} keyword(s)").ConfigureAwait(true);
    }

    /// <summary>Raised after a run so the grid's modified badges can be refreshed.</summary>
    public event Action? Applied;

    public ObservableCollection<string> KeywordsToAdd { get; } = [];

    public ObservableCollection<string> KeywordsToRemove { get; } = [];

    public ObservableCollection<BatchFailure> Failures { get; } = [];

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string? _progressLabel;

    [ObservableProperty]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _resultIsFailure;

    [ObservableProperty]
    private string? _newKeywordToAdd;

    [ObservableProperty]
    private string? _newKeywordToRemove;

    [ObservableProperty]
    private bool _replaceKeywords;

    // Each field pairs an opt-in flag with its value.
    [ObservableProperty] private bool _applyRating;
    [ObservableProperty] private int _rating;
    [ObservableProperty] private bool _applyTitle;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private bool _applyHeadline;
    [ObservableProperty] private string? _headline;
    [ObservableProperty] private bool _applyDescription;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _applyLabel;
    [ObservableProperty] private string? _label;
    [ObservableProperty] private bool _applyCreator;
    [ObservableProperty] private string? _creator;
    [ObservableProperty] private bool _applyCopyright;
    [ObservableProperty] private string? _copyright;

    // Interacting with a field opts it in. Requiring the checkbox first made the rating stars
    // unreachable — they were disabled until ticked, and clicking one was the only way to tick it.
    // Auto-ticking never turns a field *off*, so a deliberate "tick, then clear to blank" still works.
    partial void OnTitleChanged(string? value) => ApplyTitle |= !string.IsNullOrWhiteSpace(value);

    partial void OnHeadlineChanged(string? value) => ApplyHeadline |= !string.IsNullOrWhiteSpace(value);

    partial void OnDescriptionChanged(string? value) => ApplyDescription |= !string.IsNullOrWhiteSpace(value);

    partial void OnLabelChanged(string? value) => ApplyLabel |= !string.IsNullOrWhiteSpace(value);

    partial void OnCreatorChanged(string? value) => ApplyCreator |= !string.IsNullOrWhiteSpace(value);

    partial void OnCopyrightChanged(string? value) => ApplyCopyright |= !string.IsNullOrWhiteSpace(value);

    public string SelectionSummary => SelectedCount == 1
        ? "1 file selected"
        : $"{SelectedCount:N0} files selected";

    public void SetSelection(IReadOnlyList<MediaItemViewModel> items)
    {
        _items = items;
        SelectedCount = items.Count;
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanPasteKeywords));
        PasteKeywordsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddKeywordToAdd()
    {
        AddKeywords(KeywordsToAdd, NewKeywordToAdd);
        NewKeywordToAdd = string.Empty;
    }

    [RelayCommand]
    private void AddKeywordToRemove()
    {
        AddKeywords(KeywordsToRemove, NewKeywordToRemove);
        NewKeywordToRemove = string.Empty;
    }

    [RelayCommand]
    private void RemoveFromAdd(string? keyword)
    {
        if (keyword is not null)
        {
            KeywordsToAdd.Remove(keyword);
        }
    }

    [RelayCommand]
    private void RemoveFromRemove(string? keyword)
    {
        if (keyword is not null)
        {
            KeywordsToRemove.Remove(keyword);
        }
    }

    /// <summary>Accepts "a, b, c" as three keywords, matching the single-file editor.</summary>
    private static void AddKeywords(ObservableCollection<string> target, string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        foreach (var part in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!target.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(part);
            }
        }
    }

    [RelayCommand]
    private void SetRating(string? position)
    {
        if (!int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        Rating = Rating == value ? 0 : value;
        ApplyRating = true;
    }

    [RelayCommand]
    private void CancelJob() => _jobCts?.Cancel();

    [RelayCommand]
    private void ClearForm()
    {
        KeywordsToAdd.Clear();
        KeywordsToRemove.Clear();
        Failures.Clear();
        ReplaceKeywords = false;
        ApplyRating = ApplyTitle = ApplyHeadline = ApplyDescription = false;
        ApplyLabel = ApplyCreator = ApplyCopyright = false;
        Rating = 0;
        Title = Headline = Description = Label = Creator = Copyright = null;
        ResultMessage = null;
        ResultIsFailure = false;
    }

    public BatchMetadataEdit BuildEdit() => new()
    {
        ApplyTitle = ApplyTitle,
        Title = Title,
        ApplyHeadline = ApplyHeadline,
        Headline = Headline,
        ApplyDescription = ApplyDescription,
        Description = Description,
        ApplyRating = ApplyRating,
        Rating = Rating == 0 ? null : Rating,
        ApplyLabel = ApplyLabel,
        Label = Label,
        ApplyCreator = ApplyCreator,
        Creator = Creator,
        ApplyCopyright = ApplyCopyright,
        Copyright = Copyright,
        KeywordsToAdd = ReplaceKeywords ? [] : [.. KeywordsToAdd],
        KeywordsToRemove = ReplaceKeywords ? [] : [.. KeywordsToRemove],
        ReplaceKeywords = ReplaceKeywords,
        ReplacementKeywords = ReplaceKeywords ? [.. KeywordsToAdd] : []
    };

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var edit = BuildEdit();

        if (_items.Count == 0 || !edit.HasAnyChange)
        {
            ResultIsFailure = true;
            ResultMessage = "Nothing to apply — tick a field or add a keyword first.";
            return;
        }

        await RunAsync(edit, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Applies an edit to the selection, reporting progress and failures.
    ///
    /// Shared by the form and by pasting keywords: both are the same operation over the same files,
    /// and the only difference is where the edit came from.
    /// </summary>
    /// <param name="summary">
    /// Replaces the usual counts when the caller has something more useful to say. Pasting knows how
    /// many keywords it applied, which the result of the batch cannot tell.
    /// </param>
    private async Task RunAsync(BatchMetadataEdit edit, string? summary)
    {
        var files = _items.Select(i => i.File).ToList();

        _jobCts?.Dispose();
        var cts = new CancellationTokenSource();
        _jobCts = cts;

        IsRunning = true;
        Failures.Clear();
        ResultMessage = null;
        ResultIsFailure = false;
        ProgressFraction = 0;
        OnPropertyChanged(nameof(CanPasteKeywords));
        PasteKeywordsCommand.NotifyCanExecuteChanged();

        try
        {
            var progress = new Progress<JobProgress>(p =>
            {
                ProgressFraction = p.Fraction;
                ProgressLabel = $"{p.Completed:N0} of {p.Total:N0} — {p.CurrentItem}";
            });

            var result = await _batch.ApplyAsync(files, edit, progress, cts.Token).ConfigureAwait(true);

            foreach (var failure in result.Failures)
            {
                Failures.Add(failure);
            }

            ResultIsFailure = result.Failures.Count > 0;
            ResultMessage = summary is null
                ? Describe(result)
                : $"{summary} · {Describe(result)}";

            // Pending badges are per item, so refresh the ones we touched.
            var pendingPaths = _items.ToDictionary(i => i.File.FullPath, StringComparer.Ordinal);
            foreach (var item in pendingPaths.Values)
            {
                item.HasPendingChanges = true;
            }

            Applied?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch edit failed");
            ResultIsFailure = true;
            ResultMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
            ProgressLabel = null;
            OnPropertyChanged(nameof(CanPasteKeywords));
            PasteKeywordsCommand.NotifyCanExecuteChanged();
        }
    }

    private static string Describe(BatchResult result)
    {
        var parts = new List<string>();

        if (result.Changed > 0)
        {
            parts.Add($"{result.Changed:N0} file(s) modified");
        }

        if (result.Unchanged > 0)
        {
            parts.Add($"{result.Unchanged:N0} already matched");
        }

        if (result.Failures.Count > 0)
        {
            parts.Add($"{result.Failures.Count:N0} failed");
        }

        if (result.WasCancelled)
        {
            parts.Add("cancelled");
        }

        return parts.Count == 0
            ? "Nothing changed."
            : string.Join(", ", parts) + ". Nothing is written until you save.";
    }
}
