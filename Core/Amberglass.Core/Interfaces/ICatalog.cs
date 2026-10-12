using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>A keyword in the catalog, and how many files carry it.</summary>
public sealed record KeywordUsage(string Value, int Count);

/// <summary>A colour label actually in use, and how many files carry it.</summary>
public sealed record LabelUsage(string Value, int Count);

/// <summary>One file's searchable facts, ready to be written to the catalog.</summary>
public sealed record CatalogEntry(
    MediaFile File,
    EditableMetadata Metadata,
    CameraInfo Camera,
    bool HasSidecar,
    DateTimeOffset? CaptureDate,
    ImageDimensions? Dimensions = null,
    long SidecarModifiedUtc = 0);

/// <param name="SizeBytes">
/// Size on disk including SQLite's write-ahead log and shared-memory files, which can dwarf the
/// main database file and would otherwise make the reported size look wrong.
/// </param>
public sealed record CatalogStatistics(int FileCount, int KeywordCount, long SizeBytes)
{
    public static readonly CatalogStatistics Empty = new(0, 0, 0);
}

/// <summary>
/// What the catalog already knows about a file, used to decide whether it needs re-reading.
/// Modified time is seconds since the epoch, matching how it is stored.
/// </summary>
/// <param name="IndexerVersion">
/// Which generation of the indexer wrote this row. Part of the staleness test because a file that
/// has not changed can still need re-reading — when the indexer learns to extract something it did
/// not extract before, every existing row is out of date even though every file is untouched.
/// </param>
/// <param name="SidecarModifiedUtc">
/// When this file's XMP sidecar was last written, or 0 for none. Part of the staleness test because
/// metadata can change without the media file changing at all — a rating set in Lightroom goes to
/// the sidecar, and the raw file it describes is untouched.
/// </param>
public readonly record struct IndexedStamp(
    long SizeBytes,
    long ModifiedUtc,
    int IndexerVersion = 0,
    long SidecarModifiedUtc = 0);

/// <summary>
/// The local search catalog.
///
/// It is a cache of what is already in the media and its sidecars — the files stay authoritative,
/// and deleting the catalog only costs re-indexing.
/// </summary>
public interface ICatalog
{
    Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct keywords already in use, most used first, optionally scoped to a folder.
    ///
    /// For building a keyword library out of what the photographs already say rather than typing it
    /// out again. Counts are included because they are what makes the list usable: a vocabulary of
    /// four hundred keywords is mostly one-offs and typos, and the frequent ones are the real ones.
    /// </summary>
    /// <summary>
    /// Labels actually written on files, most used first. The library says what may be applied; this
    /// says what is there — including labels set by another application, which the library has never
    /// heard of and which are the ones most worth being able to find.
    /// </summary>
    /// <summary>
    /// The rating, flag and label of every indexed file under <paramref name="rootPath"/>, keyed by
    /// full path.
    ///
    /// One query for a whole folder, because the grid draws these on every tile at once and asking
    /// per file would be thousands of round trips to say "nothing" about most of them.
    /// </summary>
    Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates entries, keyed by path.</summary>
    Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// What is already indexed for the given paths. Paths absent from the result are not in the
    /// catalog. Lets indexing skip files it has already read, which is the difference between
    /// reopening a large workspace being instant and being a full re-read.
    /// </summary>
    Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <param name="rootPath">
    /// Restricts results to files beneath this folder. Null searches the whole catalog — the
    /// escape hatch for finding something outside the open workspace.
    /// </param>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        string? rootPath = null,
        int limit = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets files that no longer exist on disk, so results cannot point at nothing.</summary>
    Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <param name="Indexed">Files actually read and written to the catalog.</param>
/// <param name="Skipped">Files already current, whose metadata was not re-read.</param>
public readonly record struct IndexResult(int Indexed, int Skipped)
{
    public int Total => Indexed + Skipped;
}

/// <summary>Populates the catalog from a set of files.</summary>
public interface ICatalogIndexer
{
    Task<IndexResult> IndexAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
