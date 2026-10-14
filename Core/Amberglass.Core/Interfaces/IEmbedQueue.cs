namespace Amberglass.Core.Interfaces;

/// <summary>
/// Files the user has asked to have their sidecar's values written into the file itself.
///
/// The other half of what Sync does. The pending-change store holds edits not yet written anywhere;
/// once one is written to a sidecar it leaves that store, and a JPEG or a video whose sidecar now
/// says more than the file does had no way back into Sync. Queuing it here is that way back, and
/// it is per file and explicit: Sync embeds these whatever its global embed option says, because
/// asking for this one file is the consent that option otherwise stands in for.
/// </summary>
public interface IEmbedQueue
{
    int Count { get; }

    event EventHandler? Changed;

    bool Contains(string filePath);

    void Add(string filePath);

    void Remove(string filePath);

    IReadOnlyCollection<string> GetAll();
}
