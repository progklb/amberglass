using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

public sealed record PendingChange(string FilePath, EditableMetadata Original, EditableMetadata Edited);

public sealed class PendingChangesChangedEventArgs(string? filePath) : EventArgs
{
    /// <summary>The affected file, or null when many entries changed at once.</summary>
    public string? FilePath { get; } = filePath;
}

/// <summary>
/// The working tree of the metadata workflow: edits live here, not in the media files. Nothing in
/// this store touches the filesystem — committing is the explicit Sync operation of Phase 6.
/// </summary>
public interface IPendingChangeStore
{
    int Count { get; }

    event EventHandler<PendingChangesChangedEventArgs>? Changed;

    /// <summary>
    /// Records an edit. When <paramref name="edited"/> matches <paramref name="original"/> the entry
    /// is dropped, so editing a field and undoing it by hand leaves nothing pending.
    /// </summary>
    void Set(string filePath, EditableMetadata original, EditableMetadata edited);

    EditableMetadata? GetEdited(string filePath);

    bool HasChanges(string filePath);

    void Discard(string filePath);

    void DiscardAll();

    IReadOnlyCollection<PendingChange> GetAll();
}
