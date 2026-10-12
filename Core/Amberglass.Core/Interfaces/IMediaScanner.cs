using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

public sealed record ScanOptions
{
    public bool Recursive { get; init; } = true;

    public bool IncludeHiddenFiles { get; init; }
}

public interface IMediaScanner
{
    /// <summary>
    /// Streams supported media files under <paramref name="rootPath"/>. Results are yielded as they
    /// are discovered so callers can populate a UI without waiting for the whole tree to be walked.
    /// </summary>
    IAsyncEnumerable<MediaFile> ScanAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
