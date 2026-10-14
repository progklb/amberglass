using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// Commits pending metadata changes to disk.
///
/// Split deliberately into <see cref="PrepareAsync"/> and <see cref="ExecuteAsync"/>: the user sees
/// exactly what is about to happen — how many files, of which types, how many conflicted — before
/// anything is written. Sync is the one operation that can modify original media, so it is never
/// implicit.
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Builds the plan from the pending-change store and the embed queue, flagging conflicts and
    /// noting any work a previous interrupted run already finished.
    /// </summary>
    Task<SyncPlan> PrepareAsync(SyncOptions options, CancellationToken cancellationToken = default);

    Task<SyncResult> ExecuteAsync(
        SyncPlan plan,
        SyncOptions options,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets a previous run's progress so the next sync starts from scratch.</summary>
    void DiscardResumeState();
}
