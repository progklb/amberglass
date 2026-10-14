using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// Commits pending metadata changes to disk — the one operation that may modify original media.
///
/// Two kinds of work arrive here: edits not yet written anywhere, from the pending-change store, and
/// files whose sidecar is ahead of the file, from the embed queue. A file can be in both, and is
/// then one item that does both.
///
/// The order of business is deliberate: plan first, show the user, then write. Each file is
/// journalled the moment it succeeds, so an interrupted run resumes rather than starting over.
/// </summary>
public sealed class SyncService : ISyncService
{
    private readonly IPendingChangeStore _pending;
    private readonly IMetadataProvider _metadata;
    private readonly IMetadataWriter _writer;
    private readonly SyncJournal _journal;
    private readonly IEmbedQueue _embedQueue;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IPendingChangeStore pending,
        IMetadataProvider metadata,
        IMetadataWriter writer,
        IAppPaths paths,
        ILogger<SyncService> logger,
        IEmbedQueue? embedQueue = null)
    {
        _pending = pending;
        _metadata = metadata;
        _writer = writer;
        _logger = logger;
        _journal = new SyncJournal(paths, logger);
        _embedQueue = embedQueue ?? new EmbedQueue();
    }

    public async Task<SyncPlan> PrepareAsync(SyncOptions options, CancellationToken cancellationToken = default)
    {
        var changes = _pending.GetAll().ToDictionary(c => c.FilePath, StringComparer.Ordinal);
        var queued = _embedQueue.GetAll().ToHashSet(StringComparer.Ordinal);
        if (changes.Count == 0 && queued.Count == 0)
        {
            return SyncPlan.Empty;
        }

        var completed = _journal.LoadCompleted().ToHashSet(StringComparer.Ordinal);

        var files = changes.Keys
            .Union(queued, StringComparer.Ordinal)
            .Where(path => !completed.Contains(path))
            .Select(path => (Path: path, File: TryDescribe(path)))
            .Where(x => x.File is not null)
            .ToList();

        // Read up front, for two reasons: conflicts warn before anything is written rather than
        // half way through, and a file queued only for embedding has its values in its sidecar.
        var metadata = await _metadata
            .ReadManyAsync(files.Select(f => f.File!).ToList(), null, cancellationToken)
            .ConfigureAwait(false);

        var items = new List<SyncPlanItem>();
        foreach (var (path, file) in files)
        {
            metadata.TryGetValue(path, out var current);
            var hasConflict = current is not null && MetadataConflictDetector.Detect(current, file!.FullPath).Length > 0;
            var embedRequested = queued.Contains(path);

            if (changes.TryGetValue(path, out var change))
            {
                items.Add(new SyncPlanItem(file!, change.Edited, hasConflict)
                {
                    Before = change.Original,
                    EmbedRequested = embedRequested
                });
                continue;
            }

            // Queued for embedding only. A request that no longer has anything to do — the file
            // has caught up, or cannot be written into — is dropped rather than left to linger.
            if (current is null)
            {
                continue;
            }

            if (!MediaTypeRegistry.CanEmbedMetadata(path) || MetadataDiff.SidecarOverFile(current).IsEmpty)
            {
                _embedQueue.Remove(path);
                continue;
            }

            items.Add(new SyncPlanItem(file!, current.Effective, hasConflict)
            {
                Before = current.Embedded,
                HasEdit = false,
                EmbedRequested = true
            });
        }

        items.Sort(static (a, b) => string.Compare(a.File.FullPath, b.File.FullPath, StringComparison.OrdinalIgnoreCase));

        var resumable = completed.Where(p => changes.ContainsKey(p) || queued.Contains(p)).ToList();
        return new SyncPlan(items, resumable);
    }

    public async Task<SyncResult> ExecuteAsync(
        SyncPlan plan,
        SyncOptions options,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SyncItemResult>();
        var processed = 0;

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The journal already holds everything committed so far, so stopping here is safe
                // and the next run picks up from this point.
                return new SyncResult(results, true);
            }

            processed++;
            progress?.Report(new JobProgress(processed, plan.Count, item.File.FileName));

            if (options.SkipConflicted && item.HasConflict)
            {
                results.Add(new SyncItemResult(item.File.FullPath, SyncOutcome.Skipped,
                    "Embedded metadata and the sidecar disagree — resolve the conflict first."));
                continue;
            }

            try
            {
                var result = await SyncOneAsync(item, options, cancellationToken).ConfigureAwait(false);
                results.Add(result);

                if (result.Outcome is SyncOutcome.SidecarWritten or SyncOutcome.Embedded)
                {
                    _journal.RecordCompleted(item.File.FullPath);

                    // The file on disk now matches, so it is no longer a pending change, and a
                    // request to embed it has been met.
                    _pending.Discard(item.File.FullPath);
                    if (result.Outcome == SyncOutcome.Embedded)
                    {
                        _embedQueue.Remove(item.File.FullPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new SyncResult(results, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed for {File}", item.File.FullPath);
                results.Add(new SyncItemResult(item.File.FullPath, SyncOutcome.Failed, ex.Message));
            }
        }

        var outcome = new SyncResult(results, false);

        // A run that finished with nothing outstanding has nothing left to resume.
        if (outcome.Failures.Count == 0)
        {
            _journal.Clear();
        }

        _logger.LogInformation(
            "Sync finished: {Succeeded} written, {Skipped} skipped, {Failed} failed (embed={Embed})",
            outcome.Succeeded, outcome.Skipped, outcome.Failures.Count, options.EmbedMetadata);

        return outcome;
    }

    private async Task<SyncItemResult> SyncOneAsync(SyncPlanItem item, SyncOptions options, CancellationToken cancellationToken)
    {
        // A proprietary raw file is never embedded whatever the options say — ActionFor sees to
        // that. Its format is undocumented and differs by camera maker, and nothing in the industry
        // writes into one: Lightroom, Camera Raw and Bridge all keep user metadata beside it. The
        // sidecar is the record for that file, so stopping there is success, not a failure.
        var action = item.ActionFor(options);

        // Every edit goes to the sidecar, embedded or not: it stays the portable representation, so
        // the two never drift apart after a sync. A file queued only for embedding already has
        // these values in its sidecar, and rewriting it would only bump its date.
        if (action != SyncAction.FileOnly)
        {
            var sidecar = await _writer.WriteSidecarAsync(
                item.File,
                item.Edited,
                new SidecarWriteOptions { ValidateAfterWrite = options.ValidateAfterWriting },
                cancellationToken).ConfigureAwait(false);

            if (!sidecar.Success)
            {
                return new SyncItemResult(item.File.FullPath, SyncOutcome.Failed, sidecar.Error);
            }
        }

        if (action == SyncAction.SidecarOnly)
        {
            return new SyncItemResult(item.File.FullPath, SyncOutcome.SidecarWritten);
        }

        var embed = await _writer.WriteEmbeddedAsync(
            item.File,
            item.Edited,
            new EmbedWriteOptions
            {
                BackupOriginal = options.BackupOriginals,
                PreserveTimestamps = options.PreserveTimestamps,
                ValidateAfterWrite = options.ValidateAfterWriting
            },
            cancellationToken).ConfigureAwait(false);

        return embed.Success
            ? new SyncItemResult(item.File.FullPath, SyncOutcome.Embedded, BackupPath: embed.BackupPath)
            : new SyncItemResult(item.File.FullPath, SyncOutcome.Failed, embed.Error, embed.BackupPath);
    }

    public void DiscardResumeState() => _journal.Clear();

    private MediaFile? TryDescribe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? MediaFile.FromFileInfo(info) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not inspect {File} while planning a sync", path);
            return null;
        }
    }
}
