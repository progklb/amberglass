using System.Data;
using System.Text;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Database;

/// <summary>
/// SQLite-backed search catalog, with FTS5 for word searching and normalised keywords for exact
/// keyword filters.
/// </summary>
public sealed class SqliteCatalog : ICatalog
{
    private readonly IAppPaths _paths;
    private readonly ILogger<SqliteCatalog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// The path the schema was last applied to. Tracked rather than a simple bool so that relocating
    /// the catalog re-initialises at the new location instead of quietly using an unmigrated file.
    /// </summary>
    private string? _initialisedPath;

    public SqliteCatalog(IAppPaths paths, ILogger<SqliteCatalog> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Resolved per connection so a location change in settings takes effect immediately.</summary>
    public string CurrentPath => _paths.CatalogPath;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var path = _paths.CatalogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // journal_mode is stored in the database file, but synchronous is a property of the
        // connection and resets to FULL on every new one — which means fsync on each commit and
        // most of what WAL is for going unclaimed. NORMAL is the usual pairing, and is the right
        // trade here specifically: the catalog is an index over the files, every fact in it came
        // from a file, so the worst a lost transaction costs is re-reading those files.
        await connection.ExecuteAsync("PRAGMA synchronous=NORMAL;").ConfigureAwait(false);

        if (!string.Equals(_initialisedPath, path, StringComparison.Ordinal))
        {
            CatalogSchema.Apply(connection);
            _initialisedPath = path;
        }

        return connection;
    }

    /// <summary>
    /// Size on disk. SQLite in WAL mode keeps a journal beside the database that is frequently
    /// larger than the database itself, so reporting only the .db file would understate it badly.
    /// </summary>
    private long GetSizeOnDisk()
    {
        var path = _paths.CatalogPath;
        var total = 0L;

        foreach (var candidate in (string[])[path, path + "-wal", path + "-shm"])
        {
            try
            {
                var info = new FileInfo(candidate);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not measure {Path}", candidate);
            }
        }

        return total;
    }

    public async Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var files = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Media;").ConfigureAwait(false);
        var keywords = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Keyword;").ConfigureAwait(false);

        return new CatalogStatistics(files, keywords, GetSizeOnDisk());
    }

    public async Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertOneAsync(connection, (SqliteTransaction)transaction, entry).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task UpsertOneAsync(SqliteConnection connection, SqliteTransaction transaction, CatalogEntry entry)
    {
        var file = entry.File;
        var metadata = entry.Metadata;

        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO Media (Path, FileName, MediaType, SizeBytes, ModifiedUtc, CreatedUtc,
                               Title, Description, Headline, Label, Creator, Copyright, Rating, Flag,
                               Camera, Lens, CaptureDate, HasSidecar, IndexedUtc, IndexerVersion,
                               Width, Height, SidecarModifiedUtc)
            VALUES (@Path, @FileName, @MediaType, @SizeBytes, @ModifiedUtc, @CreatedUtc,
                    @Title, @Description, @Headline, @Label, @Creator, @Copyright, @Rating, @Flag,
                    @Camera, @Lens, @CaptureDate, @HasSidecar, @IndexedUtc, @IndexerVersion,
                    @Width, @Height, @SidecarModifiedUtc)
            ON CONFLICT(Path) DO UPDATE SET
                FileName = excluded.FileName, MediaType = excluded.MediaType,
                SizeBytes = excluded.SizeBytes, ModifiedUtc = excluded.ModifiedUtc,
                CreatedUtc = excluded.CreatedUtc, Title = excluded.Title,
                Description = excluded.Description, Headline = excluded.Headline,
                Label = excluded.Label, Creator = excluded.Creator,
                Copyright = excluded.Copyright, Rating = excluded.Rating,
                Flag = excluded.Flag,
                Camera = excluded.Camera, Lens = excluded.Lens,
                CaptureDate = excluded.CaptureDate, HasSidecar = excluded.HasSidecar,
                IndexedUtc = excluded.IndexedUtc,
                IndexerVersion = excluded.IndexerVersion,
                Width = excluded.Width, Height = excluded.Height,
                SidecarModifiedUtc = excluded.SidecarModifiedUtc
            RETURNING Id;
            """,
            new
            {
                Path = file.FullPath,
                file.FileName,
                MediaType = (int)file.MediaType,
                file.SizeBytes,
                ModifiedUtc = file.ModifiedUtc.ToUnixTimeSeconds(),
                CreatedUtc = file.CreatedUtc.ToUnixTimeSeconds(),
                metadata.Title,
                metadata.Description,
                metadata.Headline,
                metadata.Label,
                Flag = metadata.Flag is { } flag ? (int?)flag : null,
                metadata.Creator,
                metadata.Copyright,
                metadata.Rating,
                entry.Camera.Camera,
                entry.Camera.Lens,
                CaptureDate = entry.CaptureDate?.ToUnixTimeSeconds(),
                HasSidecar = entry.HasSidecar ? 1 : 0,
                IndexedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IndexerVersion = CatalogIndexer.CurrentVersion,
                Width = entry.Dimensions?.Width,
                Height = entry.Dimensions?.Height,
                entry.SidecarModifiedUtc
            },
            transaction).ConfigureAwait(false);

        // Keywords are rewritten wholesale: working out the difference would cost more than the
        // handful of rows involved.
        await connection.ExecuteAsync("DELETE FROM MediaKeyword WHERE MediaId = @id;", new { id }, transaction)
            .ConfigureAwait(false);

        foreach (var keyword in metadata.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO Keyword (Value) VALUES (@keyword);",
                new { keyword }, transaction).ConfigureAwait(false);

            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO MediaKeyword (MediaId, KeywordId)
                SELECT @id, Id FROM Keyword WHERE Value = @keyword;
                """,
                new { id, keyword }, transaction).ConfigureAwait(false);
        }

        // FTS rows share Media.Id as their rowid, so refreshing one is a delete by rowid.
        await connection.ExecuteAsync("DELETE FROM MediaSearch WHERE rowid = @id;", new { id }, transaction)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
            """
            INSERT INTO MediaSearch (rowid, FileName, Title, Description, Headline, Keywords, Creator)
            VALUES (@id, @FileName, @Title, @Description, @Headline, @Keywords, @Creator);
            """,
            new
            {
                id,
                file.FileName,
                metadata.Title,
                metadata.Description,
                metadata.Headline,
                Keywords = string.Join(' ', metadata.Keywords),
                metadata.Creator
            },
            transaction).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return new Dictionary<string, IndexedStamp>();
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<StampRow>(new CommandDefinition(
            "SELECT Path, SizeBytes, ModifiedUtc, IndexerVersion, SidecarModifiedUtc FROM Media WHERE Path IN @paths",
            new { paths },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(
            r => r.Path,
            r => new IndexedStamp(r.SizeBytes, r.ModifiedUtc, (int)r.IndexerVersion, r.SidecarModifiedUtc),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        string? rootPath = null,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var (sql, parameters) = BuildSearch(query, rootPath, limit);

        var rows = await connection.QueryAsync<SearchRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => new SearchHit(
            r.Path,
            r.FileName,
            (MediaType)r.MediaType,
            r.SizeBytes,
            DateTimeOffset.FromUnixTimeSeconds(r.ModifiedUtc),
            DateTimeOffset.FromUnixTimeSeconds(r.CreatedUtc),
            r.Rating is { } rating ? (int)rating : null,
            r.Title)).ToList();
    }

    /// <summary>
    /// Distinct keywords in use, most used first, optionally scoped to a folder.
    ///
    /// Scoped the same way search is — substr rather than LIKE, because a path may contain % or _
    /// and LIKE would read those as wildcards.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var root = NormaliseRoot(rootPath);
        var scope = root is null ? string.Empty : "\n  WHERE substr(Path, 1, @rootLength) = @root";

        // Only rows with something to say. Most files in most folders are unrated, unflagged and
        // unlabelled, and carrying them back to conclude nothing is the bulk of the work avoided.
        var predicate = root is null ? "WHERE" : "  AND";

        var sql = $"""
            SELECT Path, Rating, Flag, Label
            FROM Media{scope}
            {predicate} (Rating IS NOT NULL OR Flag IS NOT NULL OR (Label IS NOT NULL AND Label <> ''));
            """;

        var rows = await connection
            .QueryAsync<MarksRow>(new CommandDefinition(
                sql,
                new { root, rootLength = root?.Length ?? 0 },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var marks = new Dictionary<string, MediaMarks>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            marks[row.Path] = new MediaMarks(
                row.Rating is { } rating ? (int)rating : null,
                row.Flag is { } flag ? (MediaFlag)flag : MediaFlag.None,
                row.Label);
        }

        return marks;
    }

    /// <summary>
    /// Nullable throughout, since these columns are empty for most files — and <c>long</c> for both
    /// numbers: SQLite hands every integer back as Int64, and an <c>int</c> member here fails at
    /// materialisation rather than at compile time. The same trap as KeywordRow and StampRow.
    /// </summary>
    private sealed record MarksRow(string Path, long? Rating, long? Flag, string? Label);

    public async Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var root = NormaliseRoot(rootPath);

        // Grouped case-insensitively, because "Yellow" and "yellow" are one label written twice —
        // and the same reasoning the search uses when it matches them.
        var scope = root is null ? string.Empty : "\n  AND substr(Path, 1, @rootLength) = @root";

        var sql = $"""
            SELECT Label AS Value, COUNT(*) AS Count
            FROM Media
            WHERE Label IS NOT NULL AND Label <> ''{scope}
            GROUP BY LOWER(Label)
            ORDER BY Count DESC, Label COLLATE NOCASE;
            """;

        var rows = await connection
            .QueryAsync<LabelRow>(new CommandDefinition(
                sql,
                new { root, rootLength = root?.Length ?? 0 },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => new LabelUsage(r.Value, (int)r.Count)).ToList();
    }

    /// <summary>As with KeywordRow: SQLite hands COUNT(*) back as Int64.</summary>
    private sealed class LabelRow
    {
        public string Value { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    public async Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(
        string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var root = NormaliseRoot(rootPath);

        var sql = root is null
            ? """
              SELECT k.Value AS Value, COUNT(*) AS Count
              FROM MediaKeyword mk
              JOIN Keyword k ON k.Id = mk.KeywordId
              GROUP BY k.Id
              ORDER BY Count DESC, k.Value COLLATE NOCASE;
              """
            : """
              SELECT k.Value AS Value, COUNT(*) AS Count
              FROM MediaKeyword mk
              JOIN Keyword k ON k.Id = mk.KeywordId
              JOIN Media m ON m.Id = mk.MediaId
              WHERE substr(m.Path, 1, @rootLength) = @root
              GROUP BY k.Id
              ORDER BY Count DESC, k.Value COLLATE NOCASE;
              """;

        // Read into a row type with a long count, then project. SQLite returns COUNT(*) as INTEGER,
        // which is Int64, and Dapper will not bind that to a record whose constructor takes an int —
        // it fails at materialisation rather than converting.
        var rows = await connection
            .QueryAsync<KeywordRow>(new CommandDefinition(
                sql,
                new { root, rootLength = root?.Length ?? 0 },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .Select(row => new KeywordUsage(row.Value, (int)Math.Min(row.Count, int.MaxValue)))
            .ToList();
    }

    private sealed class KeywordRow
    {
        public string Value { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    /// <summary>
    /// Normalises a workspace root for prefix matching, or returns null when the search is not
    /// scoped. The trailing separator is the whole point: without it a root of "/photos/nam" would
    /// also match "/photos/namibia".
    /// </summary>
    internal static string? NormaliseRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        return rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Builds the WHERE clause from the parsed query. Every value is parameterised — a search box is
    /// user input, and it is not going anywhere near string-concatenated SQL.
    /// </summary>
    internal static (string Sql, DynamicParameters Parameters) BuildSearch(
        SearchQuery query,
        string? rootPath,
        int limit)
    {
        var sql = new StringBuilder("""
            SELECT m.Path, m.FileName, m.MediaType, m.SizeBytes, m.ModifiedUtc, m.CreatedUtc, m.Rating, m.Title
            FROM Media m
            WHERE 1 = 1
            """);

        var parameters = new DynamicParameters();

        if (NormaliseRoot(rootPath) is { } root)
        {
            // substr rather than LIKE: a path may contain % or _, which LIKE would treat as
            // wildcards, and escaping them correctly is easy to get subtly wrong. The trailing
            // separator that NormaliseRoot guarantees is what stops /photos/nam matching
            // /photos/namibia.
            sql.Append("\n  AND substr(m.Path, 1, @rootLength) = @root");
            parameters.Add("root", root);
            parameters.Add("rootLength", root.Length);
        }

        if (!query.FreeText.IsDefaultOrEmpty)
        {
            sql.Append("\n  AND m.Id IN (SELECT rowid FROM MediaSearch WHERE MediaSearch MATCH @match)");
            parameters.Add("match", BuildMatchExpression(query.FreeText));
        }

        if (!query.Kinds.IsDefaultOrEmpty)
        {
            // Any of the chosen kinds, so the clauses are ORed inside one bracket and ANDed with
            // everything else. Raw has no column of its own — the catalog only knows image from
            // video — so it is drawn from the extension, against the same list the rest of the
            // application uses rather than a second copy of it.
            var clauses = query.Kinds.Select(kind => kind switch
            {
                MediaKind.Video => $"m.MediaType = {(int)MediaType.Video}",
                MediaKind.Raw => $"(m.MediaType = {(int)MediaType.Image} AND {RawExtensionTest})",
                _ => $"(m.MediaType = {(int)MediaType.Image} AND NOT {RawExtensionTest})"
            });

            sql.Append($"\n  AND ({string.Join(" OR ", clauses)})");
        }

        if (!query.Orientations.IsDefaultOrEmpty)
        {
            // Compared here rather than stored as a third column: the dimensions go in already
            // turned the right way up, so which shape a picture is follows from two numbers and
            // needs nothing kept in step with them. Files indexed before dimensions were recorded
            // have none, and are excluded rather than guessed at.
            var shapes = query.Orientations.Select(orientation => orientation switch
            {
                MediaOrientation.Portrait => "m.Height > m.Width",
                MediaOrientation.Square => "m.Height = m.Width",
                _ => "m.Width > m.Height"
            });

            sql.Append($"\n  AND m.Width IS NOT NULL AND m.Height IS NOT NULL");
            sql.Append($"\n  AND ({string.Join(" OR ", shapes)})");
        }

        if (query.Rating is { } rating)
        {
            sql.Append($"\n  AND m.Rating IS NOT NULL AND m.Rating {ToSql(rating.Operator)} @rating");
            parameters.Add("rating", rating.Value);
        }

        if (query.CaptureDate is { } date)
        {
            sql.Append($"\n  AND m.CaptureDate IS NOT NULL AND m.CaptureDate {ToSql(date.Operator)} @captureDate");
            parameters.Add("captureDate", date.Value.ToUnixTimeSeconds());
        }

        for (var i = 0; i < query.Keywords.Length; i++)
        {
            // One EXISTS per filter gives AND across them — all must be satisfied — while IN gives
            // OR inside one, so "k:sand k:dust" wants both and "k:sand,dust" wants either.
            var placeholders = new List<string>();

            for (var j = 0; j < query.Keywords[i].AnyOf.Length; j++)
            {
                var name = $"keyword{i}_{j}";
                placeholders.Add($"@{name}");
                parameters.Add(name, query.Keywords[i].AnyOf[j]);
            }

            sql.Append($"""

                  AND EXISTS (SELECT 1 FROM MediaKeyword mk
                              JOIN Keyword k ON k.Id = mk.KeywordId
                              WHERE mk.MediaId = m.Id AND k.Value IN ({string.Join(", ", placeholders)}))
                """);
        }

        if (!query.Labels.IsDefaultOrEmpty || query.IncludeUnlabelled)
        {
            // Lowered on both sides: labels are typed by hand and by other applications, so "Yellow"
            // and "yellow" are the same label and a case-sensitive IN would quietly miss half of them.
            var placeholders = new List<string>();

            for (var i = 0; i < query.Labels.Length; i++)
            {
                placeholders.Add($"@label{i}");
                parameters.Add($"label{i}", query.Labels[i].ToLowerInvariant());
            }

            var clauses = new List<string>();

            if (placeholders.Count > 0)
            {
                clauses.Add($"LOWER(m.Label) IN ({string.Join(", ", placeholders)})");
            }

            // A file with no label has NULL here, and some applications write an empty string —
            // both mean unlabelled, and an IN test would match neither.
            if (query.IncludeUnlabelled)
            {
                clauses.Add("(m.Label IS NULL OR m.Label = '')");
            }

            sql.Append($"\n  AND ({string.Join(" OR ", clauses)})");
        }

        if (!query.Flags.IsDefaultOrEmpty)
        {
            // "None" has to include files with no flag at all as well as files explicitly cleared:
            // an unjudged photograph and one somebody un-flagged are the same thing to look at next.
            var clauses = new List<string>();

            for (var i = 0; i < query.Flags.Length; i++)
            {
                parameters.Add($"flag{i}", (int)query.Flags[i]);
                clauses.Add(query.Flags[i] == MediaFlag.None
                    ? $"(m.Flag IS NULL OR m.Flag = @flag{i})"
                    : $"m.Flag = @flag{i}");
            }

            sql.Append($"\n  AND ({string.Join(" OR ", clauses)})");
        }

        for (var i = 0; i < query.FileNames.Length; i++)
        {
            sql.Append($"\n  AND m.FileName LIKE @fileName{i}");
            parameters.Add($"fileName{i}", $"%{query.FileNames[i]}%");
        }

        for (var i = 0; i < query.Cameras.Length; i++)
        {
            sql.Append($"\n  AND m.Camera LIKE @camera{i}");
            parameters.Add($"camera{i}", $"%{query.Cameras[i]}%");
        }

        for (var i = 0; i < query.Lenses.Length; i++)
        {
            sql.Append($"\n  AND m.Lens LIKE @lens{i}");
            parameters.Add($"lens{i}", $"%{query.Lenses[i]}%");
        }

        sql.Append("\nORDER BY m.FileName COLLATE NOCASE\nLIMIT @limit;");
        parameters.Add("limit", limit);

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Turns bare words into an FTS5 expression. Each term is quoted (so punctuation cannot be read
    /// as FTS syntax) and given a prefix wildcard, because "namib" should find "Namibia".
    /// </summary>
    internal static string BuildMatchExpression(IEnumerable<string> terms)
        => string.Join(" AND ", terms
            .Select(t => t.Replace("\"", string.Empty).Trim())
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*"));

    /// <summary>
    /// True for a raw file, by extension. Built once from the registry so adding a raw format in one
    /// place teaches the search about it too. The values are extensions from a hard-coded list, not
    /// user input, so composing them into the SQL introduces nothing to inject.
    /// </summary>
    private static readonly string RawExtensionTest =
        "(" + string.Join(" OR ", MediaTypeRegistry.RawFileExtensions
            .Select(extension => $"LOWER(m.FileName) LIKE '%{extension.ToLowerInvariant()}'")) + ")";

    private static string ToSql(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        _ => "="
    };

    public async Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var paths = (await connection.QueryAsync<MediaRow>("SELECT Id, Path FROM Media;")
            .ConfigureAwait(false)).ToList();

        var missing = paths.Where(p => !File.Exists(p.Path)).Select(p => p.Id).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync("DELETE FROM Media WHERE Id IN @missing;", new { missing }, (SqliteTransaction)transaction)
                .ConfigureAwait(false);
            await connection.ExecuteAsync("DELETE FROM MediaSearch WHERE rowid IN @missing;", new { missing }, (SqliteTransaction)transaction)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Removed {Count} catalog entries for files that no longer exist", missing.Count);
        return missing.Count;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var table in (string[])["MediaKeyword", "Keyword", "MediaSearch", "Media"])
            {
                await connection.ExecuteAsync($"DELETE FROM {table};").ConfigureAwait(false);
            }

            // Deleting rows does not shrink the file, and a "Clear" that reports the same size
            // afterwards looks broken. Checkpoint the journal, then reclaim the space.
            await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
            await connection.ExecuteAsync("VACUUM;").ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// SQLite hands back every integer as a 64-bit value, so the columns are read as such and
    /// narrowed on the way out — declaring them as int makes Dapper fail to find a matching
    /// constructor at all.
    /// </summary>
    private sealed record SearchRow(
        string Path,
        string FileName,
        long MediaType,
        long SizeBytes,
        long ModifiedUtc,
        long CreatedUtc,
        long? Rating,
        string? Title);

    /// <summary>Test hook: the number of media-to-keyword links, used to check for orphans.</summary>
    internal async Task<int> CountKeywordLinksAsync()
    {
        await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaKeyword;").ConfigureAwait(false);
    }

    private sealed record MediaRow(long Id, string Path);

    // long, not int: SQLite hands back every integer column as Int64, and a mismatched constructor
    // makes Dapper fail to materialise the row at all.
    /// <summary>
    /// IndexerVersion is a long because SQLite hands INTEGER back as Int64, and Dapper will not
    /// materialise it into an int — it fails at run time rather than compile time, so the narrowing
    /// happens here where it is visible.
    /// </summary>
    private sealed record StampRow(string Path, long SizeBytes, long ModifiedUtc, long IndexerVersion, long SidecarModifiedUtc);
}
