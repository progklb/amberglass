using System.Globalization;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging;

namespace Amberglass.Database;

/// <summary>
/// Reads metadata for a set of files and writes it into the catalog.
///
/// Work is chunked so a large library reports progress and can be cancelled part-way without losing
/// what it has already indexed — the alternative, one enormous transaction, would make a cancelled
/// index of 50,000 files worth nothing.
/// </summary>
public sealed class CatalogIndexer : ICatalogIndexer
{
    private const int ChunkSize = 100;

    private readonly IMetadataProvider _metadata;
    private readonly ICatalog _catalog;
    private readonly ILogger<CatalogIndexer> _logger;

    public CatalogIndexer(IMetadataProvider metadata, ICatalog catalog, ILogger<CatalogIndexer> logger)
    {
        _metadata = metadata;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IndexResult> IndexAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0 || !_metadata.IsAvailable)
        {
            return default;
        }

        var indexed = 0;
        var skipped = 0;
        var seen = 0;

        foreach (var chunk in files.Chunk(ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ask what is already known before reading anything. Metadata reads shell out to
            // ExifTool and dominate the cost; this query does not.
            var known = await _catalog
                .GetIndexedStampsAsync(chunk.Select(f => f.FullPath).ToList(), cancellationToken)
                .ConfigureAwait(false);

            // Read once and reused for both the staleness test and the row written afterwards.
            // Taken before the metadata is read, deliberately: if the sidecar changes while it is
            // being read, the stored stamp is the older one and the file is re-read next time
            // rather than being recorded as current with stale contents.
            var sidecars = chunk.ToDictionary(
                file => file.FullPath,
                file => XmpSidecar.LastWrittenUtc(file.FullPath),
                StringComparer.Ordinal);

            var stale = chunk.Where(file => NeedsIndexing(file, known, sidecars[file.FullPath])).ToArray();

            seen += chunk.Length;
            skipped += chunk.Length - stale.Length;

            if (stale.Length == 0)
            {
                progress?.Report(new JobProgress(seen, files.Count, chunk[^1].FileName));
                continue;
            }

            var metadata = await _metadata.ReadManyAsync(stale, null, cancellationToken).ConfigureAwait(false);

            var entries = new List<CatalogEntry>(stale.Length);
            foreach (var file in stale)
            {
                if (!metadata.TryGetValue(file.FullPath, out var read))
                {
                    continue;
                }

                entries.Add(new CatalogEntry(
                    file,
                    read.Effective,
                    read.Camera,
                    read.HasSidecar,
                    ParseCaptureDate(read.Camera.CaptureDate),
                    read.Dimensions,
                    sidecars[file.FullPath]));
            }

            await _catalog.UpsertAsync(entries, cancellationToken).ConfigureAwait(false);

            indexed += entries.Count;
            progress?.Report(new JobProgress(seen, files.Count, chunk[^1].FileName));
        }

        _logger.LogInformation(
            "Indexed {Count} file(s), skipped {Skipped} already current, of {Total}",
            indexed,
            skipped,
            files.Count);

        return new IndexResult(indexed, skipped);
    }

    /// <summary>
    /// True when the catalog has no record of the file, or its size or modified time has moved.
    ///
    /// Comparing size *and* modified time rather than either alone: an edit that preserves the
    /// timestamp usually changes the size, and one that preserves the size usually changes the
    /// timestamp. Content hashing would be exact, but reading every byte of every file would cost
    /// far more than the metadata read it is trying to avoid.
    /// </summary>
    /// <summary>
    /// What this generation of the indexer extracts.
    ///
    /// Bumped whenever the meaning of an indexed row changes, which makes every existing row stale
    /// and re-read. Version 1 added the cull flag, stopped storing a rejected file's rating as zero,
    /// and put the filename in the search index — none of which any file's own timestamp reflects,
    /// so without this a catalog would have kept answering with what an older indexer understood.
    /// </summary>
    /// <summary>
    /// Version 2 records each picture's dimensions, so orientation can be searched. Every existing
    /// row was written without them and has to be read again, which is what this number is for.
    /// </summary>
    /// <summary>
    /// Version 3 reads Lightroom's cull flag. A folder culled in Lightroom and indexed before this
    /// was recorded as unflagged, with the sidecar's timestamp already noted as seen — so nothing
    /// short of this number would have made the catalog look at it again.
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Whether a file has to be read again.
    ///
    /// The sidecar's timestamp is part of this and has to be, because metadata can change without
    /// the media file changing at all: a rating set in Lightroom, a label set in Bridge, or a save
    /// from this application all go to the sidecar and leave the raw file untouched. Judging
    /// staleness by the media file alone meant those edits were invisible to the catalog until
    /// something else happened to the file — which is how a rejected photograph could sit in the
    /// catalog unflagged and never turn up in a search for rejects.
    /// </summary>
    internal static bool NeedsIndexing(
        MediaFile file,
        IReadOnlyDictionary<string, IndexedStamp> known,
        long sidecarModifiedUtc)
        => !known.TryGetValue(file.FullPath, out var stamp)
           || stamp.SizeBytes != file.SizeBytes
           || stamp.ModifiedUtc != file.ModifiedUtc.ToUnixTimeSeconds()
           || stamp.IndexerVersion != CurrentVersion
           || stamp.SidecarModifiedUtc != sidecarModifiedUtc;

    /// <summary>
    /// EXIF dates use colons between date parts — "2024:06:01 09:15:22" — which no standard parser
    /// accepts, so the date portion is normalised before parsing.
    /// </summary>
    internal static DateTimeOffset? ParseCaptureDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var separator = text.IndexOf(' ');

        if (separator > 0)
        {
            text = string.Concat(text[..separator].Replace(':', '-'), text[separator..]);
        }
        else
        {
            text = text.Replace(':', '-');
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
