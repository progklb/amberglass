using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

public sealed record JobProgress(int Completed, int Total, string? CurrentItem)
{
    public double Fraction => Total > 0 ? Completed / (double)Total : 0;
}

public sealed record BatchFailure(string FilePath, string Reason);

public sealed record BatchResult(
    int Changed,
    int Unchanged,
    IReadOnlyList<BatchFailure> Failures,
    bool WasCancelled)
{
    public int Total => Changed + Unchanged + Failures.Count;
}

/// <summary>
/// Applies a metadata edit across many files.
///
/// Nothing is written to disk: each file's edit lands in the pending-change store, exactly as a
/// single-file edit does. Committing stays the explicit, separate act it has been since Phase 3 —
/// batch editing must not become a back door around that.
/// </summary>
public interface IBatchMetadataService
{
    Task<BatchResult> ApplyAsync(
        IReadOnlyList<MediaFile> files,
        BatchMetadataEdit edit,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
