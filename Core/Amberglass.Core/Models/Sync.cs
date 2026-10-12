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

/// <summary>One file queued for sync, with everything needed to decide whether to include it.</summary>
public sealed record SyncPlanItem(
    MediaFile File,
    EditableMetadata Edited,
    bool HasConflict)
{
    public string Extension => Path.GetExtension(File.FullPath).TrimStart('.').ToUpperInvariant();
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

    public int Skipped => Items.Count(i => i.Outcome == SyncOutcome.Skipped);

    public IReadOnlyList<SyncItemResult> Failures => Items.Where(i => i.Outcome == SyncOutcome.Failed).ToList();
}
