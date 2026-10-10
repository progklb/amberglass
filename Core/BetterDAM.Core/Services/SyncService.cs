using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Core.Services;

/// <summary>
/// Commits pending metadata changes to disk — the one operation that may modify original media.
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
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IPendingChangeStore pending,
        IMetadataProvider metadata,
        IMetadataWriter writer,
        IAppPaths paths,
        ILogger<SyncService> logger)
    {
        _pending = pending;
        _metadata = metadata;
        _writer = writer;
        _logger = logger;
        _journal = new SyncJournal(paths, logger);
    }

    public async Task<SyncPlan> PrepareAsync(SyncOptions options, CancellationToken cancellationToken = default)
    {
        var changes = _pending.GetAll();
        if (changes.Count == 0)
        {
            return SyncPlan.Empty;
        }

        var completed = _journal.LoadCompleted().ToHashSet(StringComparer.Ordinal);

        var files = changes
            .Where(c => !completed.Contains(c.FilePath))
            .Select(c => (Change: c, File: TryDescribe(c.FilePath)))
            .Where(x => x.File is not null)
            .ToList();

        // Conflicts are read up front so the summary can warn before anything is written, rather
        // than discovering mid-run that half the selection disagreed with its sidecar.
        var metadata = await _metadata
            .ReadManyAsync(files.Select(f => f.File!).ToList(), null, cancellationToken)
            .ConfigureAwait(false);

        var items = new List<SyncPlanItem>();
        foreach (var (change, file) in files)
        {
            var hasConflict = metadata.TryGetValue(change.FilePath, out var current) &&
                              MetadataConflictDetector.Detect(current, file!.FullPath).Length > 0;

            items.Add(new SyncPlanItem(file!, change.Edited, hasConflict));
        }

        var resumable = completed.Where(p => changes.Any(c => c.FilePath == p)).ToList();
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

                    // The file on disk now matches, so it is no longer a pending change.
                    _pending.Discard(item.File.FullPath);
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
        // The sidecar is always written: it stays the portable representation even when the
        // metadata is also embedded, so the two never drift apart after a sync.
        var sidecar = await _writer.WriteSidecarAsync(
            item.File,
            item.Edited,
            new SidecarWriteOptions { ValidateAfterWrite = options.ValidateAfterWriting },
            cancellationToken).ConfigureAwait(false);

        if (!sidecar.Success)
        {
            return new SyncItemResult(item.File.FullPath, SyncOutcome.Failed, sidecar.Error);
        }

        if (!options.EmbedMetadata)
        {
            return new SyncItemResult(item.File.FullPath, SyncOutcome.SidecarWritten);
        }

        // A proprietary raw file is left alone whatever the options say. Its format is undocumented
        // and differs by camera maker, and nothing in the industry writes into one — Lightroom,
        // Camera Raw and Bridge all keep user metadata beside it instead. The sidecar written above
        // is the record for this file, so there is nothing further to do and nothing has failed.
        if (!MediaTypeRegistry.CanEmbedMetadata(item.File.FullPath))
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
