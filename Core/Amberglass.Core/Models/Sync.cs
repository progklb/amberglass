namespace Amberglass.Core.Models;

/// <summary>
/// What a sync run is allowed to do.
///
/// <see cref="EmbedMetadata"/> is the consequential one: it is the only setting in the application
/// that permits writing to the user's original media, and it is off by default.
/// </summary>
public sealed record SyncOptions
{
    /// <summary>
    /// Write metadata into the media files themselves. When false, sync writes XMP sidecars only and
    /// the originals are never opened for writing.
    /// </summary>
    public bool EmbedMetadata { get; init; }

    /// <summary>Keep a copy of each original before embedding. Only meaningful when embedding.</summary>
    public bool BackupOriginals { get; init; } = true;

    /// <summary>
    /// Keep the filesystem modification time. This is the whole reason the project exists — Bridge
    /// changing timestamps on every keyword edit was the original complaint.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>Read each file back after writing and confirm it says what was asked for.</summary>
    public bool ValidateAfterWriting { get; init; } = true;

    /// <summary>Leave files whose embedded metadata disagrees with their sidecar untouched.</summary>
    public bool SkipConflicted { get; init; } = true;
}

/// <summary>What a sync run will do to one file.</summary>
public enum SyncAction
{
    /// <summary>Write the XMP sidecar; the file itself is not opened for writing.</summary>
    SidecarOnly,

    /// <summary>Write the sidecar and embed the same values into the file.</summary>
    SidecarAndFile,

    /// <summary>The sidecar already says it; copy its values into the file.</summary>
    FileOnly
}

/// <summary>One file queued for sync, with everything needed to decide whether to include it.</summary>
public sealed record SyncPlanItem(
    MediaFile File,
    EditableMetadata Edited,
    bool HasConflict)
{
    public string Extension => Path.GetExtension(File.FullPath).TrimStart('.').ToUpperInvariant();

    /// <summary>
    /// What the values were before: the pending edit's starting point, or for a file queued only to
    /// be embedded, what the file itself says now. The other side of the review's before and after.
    /// </summary>
    public EditableMetadata Before { get; init; } = EditableMetadata.Empty;

    /// <summary>False for a file queued only to be embedded: nothing new is going to its sidecar.</summary>
    public bool HasEdit { get; init; } = true;

    /// <summary>
    /// The user asked for this file to be embedded, from the inspector. Wins over the global option,
    /// which is a default for files nobody has said anything about.
    /// </summary>
    public bool EmbedRequested { get; init; }

    public SyncAction ActionFor(SyncOptions options)
    {
        var embed = MediaTypeRegistry.CanEmbedMetadata(File.FullPath) && (options.EmbedMetadata || EmbedRequested);

        return !HasEdit ? SyncAction.FileOnly
            : embed ? SyncAction.SidecarAndFile
            : SyncAction.SidecarOnly;
    }
}

/// <summary>
/// The summary shown before anything is written — the README's "45 JPG / 120 MP4 / 13 CR3".
/// </summary>
public sealed record SyncPlan(
    IReadOnlyList<SyncPlanItem> Items,
    IReadOnlyList<string> AlreadyCompleted)
{
    public static readonly SyncPlan Empty = new([], []);

    public int Count => Items.Count;

    public int ConflictCount => Items.Count(i => i.HasConflict);

    /// <summary>Counts per file type, most numerous first.</summary>
    public IReadOnlyList<(string Extension, int Count)> ByExtension => Items
        .GroupBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
        .Select(g => (g.Key, g.Count()))
        .OrderByDescending(g => g.Item2)
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>True when an interrupted run left work already done.</summary>
    public bool IsResuming => AlreadyCompleted.Count > 0;
}

public enum SyncOutcome
{
    SidecarWritten,
    Embedded,
    Skipped,
    Failed
}

public sealed record SyncItemResult(string FilePath, SyncOutcome Outcome, string? Error = null, string? BackupPath = null);

public sealed record SyncResult(IReadOnlyList<SyncItemResult> Items, bool WasCancelled)
{
    public int Succeeded => Items.Count(i => i.Outcome is SyncOutcome.SidecarWritten or SyncOutcome.Embedded);

    public int Embedded => Items.Count(i => i.Outcome == SyncOutcome.Embedded);

    public int Skipped => Items.Count(i => i.Outcome == SyncOutcome.Skipped);

    public IReadOnlyList<SyncItemResult> Failures => Items.Where(i => i.Outcome == SyncOutcome.Failed).ToList();
}
