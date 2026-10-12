using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// Applies a metadata edit across a selection.
///
/// The expensive part is not the edit — it is reading each file's current metadata, which is needed
/// as the baseline so a pending change can be recorded (and so an edit that changes nothing records
/// nothing). Reads therefore go through the provider's batched path, and the whole operation is
/// cancellable and reports progress.
/// </summary>
public sealed class BatchMetadataService : IBatchMetadataService
{
    private readonly IMetadataProvider _metadata;
    private readonly IPendingChangeStore _pending;
    private readonly ILogger<BatchMetadataService> _logger;

    public BatchMetadataService(
        IMetadataProvider metadata,
        IPendingChangeStore pending,
        ILogger<BatchMetadataService> logger)
    {
        _metadata = metadata;
        _pending = pending;
        _logger = logger;
    }

    public async Task<BatchResult> ApplyAsync(
        IReadOnlyList<MediaFile> files,
        BatchMetadataEdit edit,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<BatchFailure>();

        if (files.Count == 0 || !edit.HasAnyChange)
        {
            return new BatchResult(0, 0, failures, false);
        }

        // Reading is the bulk of the work, so it drives the progress bar.
        var readProgress = progress is null
            ? null
            : new Progress<int>(done => progress.Report(new JobProgress(done, files.Count, "Reading metadata")));

        IReadOnlyDictionary<string, MediaMetadata> current;
        try
        {
            current = await _metadata.ReadManyAsync(files, readProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BatchResult(0, 0, failures, true);
        }

        var changed = 0;
        var unchanged = 0;
        var processed = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new BatchResult(changed, unchanged, failures, true);
            }

            processed++;
            progress?.Report(new JobProgress(processed, files.Count, file.FileName));

            if (!current.TryGetValue(file.FullPath, out var metadata))
            {
                // A file whose metadata could not be read has no trustworthy baseline, and guessing
                // one risks recording an edit that silently discards values already on disk.
                failures.Add(new BatchFailure(file.FullPath, "Metadata could not be read."));
                continue;
            }

            try
            {
                // Build on any edit already pending for this file rather than on disk alone, so two
                // successive batch operations compose instead of the second undoing the first.
                var baseline = metadata.Effective;
                var starting = _pending.GetEdited(file.FullPath) ?? baseline;
                var edited = edit.ApplyTo(starting);

                if (edited.ValueEquals(baseline))
                {
                    // Matches what is on disk, so any previous pending edit is now redundant.
                    _pending.Discard(file.FullPath);
                    unchanged++;
                    continue;
                }

                _pending.Set(file.FullPath, baseline, edited);
                changed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch edit failed for {File}", file.FullPath);
                failures.Add(new BatchFailure(file.FullPath, ex.Message));
            }
        }

        _logger.LogInformation(
            "Batch edit applied to {Changed} file(s), {Unchanged} unchanged, {Failed} failed",
            changed, unchanged, failures.Count);

        return new BatchResult(changed, unchanged, failures, false);
    }
}
