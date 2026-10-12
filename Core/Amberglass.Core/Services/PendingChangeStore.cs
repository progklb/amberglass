using System.Collections.Concurrent;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;

namespace Amberglass.Core.Services;

public sealed class PendingChangeStore : IPendingChangeStore
{
    private readonly ConcurrentDictionary<string, PendingChange> _changes = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the working tree is kept so it survives the application dying. Optional: without one
    /// the store is memory-only, which is what a test wants and what the store was until edits
    /// started living long enough to be worth losing.
    /// </summary>
    private readonly PendingChangeJournal? _journal;

    public PendingChangeStore(PendingChangeJournal? journal = null)
    {
        _journal = journal;

        foreach (var change in journal?.Restore() ?? [])
        {
            _changes[change.FilePath] = change;
        }
    }

    public int Count => _changes.Count;

    public event EventHandler<PendingChangesChangedEventArgs>? Changed;

    public void Set(string filePath, EditableMetadata original, EditableMetadata edited)
    {
        if (edited.ValueEquals(original))
        {
            Discard(filePath);
            return;
        }

        var change = new PendingChange(filePath, original, edited);
        _changes[filePath] = change;
        _journal?.RecordSet(change);
        Changed?.Invoke(this, new PendingChangesChangedEventArgs(filePath));
    }

    public EditableMetadata? GetEdited(string filePath)
        => _changes.TryGetValue(filePath, out var change) ? change.Edited : null;

    public bool HasChanges(string filePath) => _changes.ContainsKey(filePath);

    public void Discard(string filePath)
    {
        if (_changes.TryRemove(filePath, out _))
        {
            _journal?.RecordDiscard(filePath);
            Changed?.Invoke(this, new PendingChangesChangedEventArgs(filePath));
        }
    }

    public void DiscardAll()
    {
        if (_changes.IsEmpty)
        {
            return;
        }

        _changes.Clear();

        // Rewritten rather than a discard per path: this is the one operation that empties the tree,
        // and compacting here is what keeps the journal from growing across a long session.
        _journal?.Compact([]);
        Changed?.Invoke(this, new PendingChangesChangedEventArgs(null));
    }

    public IReadOnlyCollection<PendingChange> GetAll() => _changes.Values.ToList();
}
