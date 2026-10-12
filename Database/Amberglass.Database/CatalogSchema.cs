using Microsoft.Data.Sqlite;

namespace Amberglass.Database;

/// <summary>
/// Creates and migrates the catalog schema.
///
/// Versioned from the start so later phases can add columns without asking anyone to delete their
/// catalog — the whole point of a migration step is that upgrading is never a data-loss event.
/// </summary>
internal static class CatalogSchema
{
    public const int CurrentVersion = 5;

    public static void Apply(SqliteConnection connection)
    {
        // WAL lets indexing write while the UI reads, which is the normal state of this application.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");

        Execute(connection, "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);");

        var version = GetVersion(connection);
        if (version >= CurrentVersion)
        {
            return;
        }

        if (version < 1)
        {
            ApplyVersion1(connection);
        }

        if (version < 2)
        {
            ApplyVersion2(connection);
        }

        if (version < 3)
        {
            ApplyVersion3(connection);
        }

        if (version < 4)
        {
            ApplyVersion4(connection);
        }

        if (version < 5)
        {
            ApplyVersion5(connection);
        }

        SetVersion(connection, CurrentVersion);
    }

    /// <summary>
    /// Adds the cull flag. A migration rather than a column in version 1, so an existing catalog
    /// gains it without anyone being asked to reindex — the flag simply reads as null until the file
    /// is next indexed, which is what "not yet judged" means anyway.
    /// </summary>
    private static void ApplyVersion2(SqliteConnection connection)
    {
        if (!HasColumn(connection, "Media", "Flag"))
        {
            Execute(connection, "ALTER TABLE Media ADD COLUMN Flag INTEGER;");
        }

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_Flag ON Media(Flag);");
    }

    /// <summary>
    /// Records which generation of the indexer wrote each row, and puts the filename into the search
    /// index.
    ///
    /// The version column is what makes a migration like the last one actually take effect. Adding
    /// the cull flag gave every existing row a null flag, and nothing would ever have filled it in:
    /// files had not changed, so the indexer had no reason to re-read them, and a search for
    /// rejected photographs answered "none" on a workspace full of them. Existing rows default to 0
    /// and so are all stale against the current indexer, which re-reads them once.
    ///
    /// FTS5 tables cannot gain a column, so the search index is dropped and rebuilt by that same
    /// re-read rather than migrated.
    /// </summary>
    private static void ApplyVersion3(SqliteConnection connection)
    {
        if (!HasColumn(connection, "Media", "IndexerVersion"))
        {
            Execute(connection, "ALTER TABLE Media ADD COLUMN IndexerVersion INTEGER NOT NULL DEFAULT 0;");
        }

        Execute(connection, "DROP TABLE IF EXISTS MediaSearch;");
        Execute(connection, """
            CREATE VIRTUAL TABLE MediaSearch USING fts5(
                FileName, Title, Description, Headline, Keywords, Creator
            );
            """);
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds the picture's dimensions, so orientation can be searched.
    ///
    /// Stored the right way up — a camera held on its side records landscape numbers and a tag
    /// saying to turn the result — so the correction happens once, on the way in, and every query
    /// after it is a plain comparison of two numbers.
    /// </summary>
    private static void ApplyVersion4(SqliteConnection connection)
    {
        if (!HasColumn(connection, "Media", "Width"))
        {
            Execute(connection, "ALTER TABLE Media ADD COLUMN Width INTEGER;");
        }

        if (!HasColumn(connection, "Media", "Height"))
        {
            Execute(connection, "ALTER TABLE Media ADD COLUMN Height INTEGER;");
        }
    }

    /// <summary>
    /// Records when each file's XMP sidecar was last written, so a change to it can be noticed.
    ///
    /// Nothing needs re-reading to fill this in. Existing rows have 0, which differs from the real
    /// timestamp of any sidecar that exists, so exactly the files that have one are re-read — and
    /// files with no sidecar, which is most of them, stay 0 and are left alone.
    /// </summary>
    private static void ApplyVersion5(SqliteConnection connection)
    {
        if (!HasColumn(connection, "Media", "SidecarModifiedUtc"))
        {
            Execute(connection, "ALTER TABLE Media ADD COLUMN SidecarModifiedUtc INTEGER NOT NULL DEFAULT 0;");
        }
    }

    private static void ApplyVersion1(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Media (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Path         TEXT    NOT NULL UNIQUE,
                FileName     TEXT    NOT NULL,
                MediaType    INTEGER NOT NULL,
                SizeBytes    INTEGER NOT NULL,
                ModifiedUtc  INTEGER NOT NULL,
                CreatedUtc   INTEGER NOT NULL,
                Title        TEXT,
                Description  TEXT,
                Headline     TEXT,
                Label        TEXT,
                Creator      TEXT,
                Copyright    TEXT,
                Rating       INTEGER,
                Camera       TEXT,
                Lens         TEXT,
                CaptureDate  INTEGER,
                HasSidecar   INTEGER NOT NULL DEFAULT 0,
                IndexedUtc   INTEGER NOT NULL
            );
            """);

        // Filters people actually combine: type, rating and date.
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_MediaType ON Media(MediaType);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_Rating ON Media(Rating);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_CaptureDate ON Media(CaptureDate);");

        // Keywords are normalised so keyword:x is an exact indexed lookup rather than a text scan.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Keyword (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Value TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS MediaKeyword (
                MediaId   INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
                KeywordId INTEGER NOT NULL REFERENCES Keyword(Id) ON DELETE CASCADE,
                PRIMARY KEY (MediaId, KeywordId)
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_MediaKeyword_KeywordId ON MediaKeyword(KeywordId);");

        // Full text over the fields worth searching by words. rowid is kept equal to Media.Id so a
        // re-index is a delete-by-rowid rather than a scan.
        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS MediaSearch USING fts5(
                FileName, Title, Description, Headline, Keywords, Creator
            );
            """);
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        Execute(connection, "DELETE FROM SchemaVersion;");

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SchemaVersion (Version) VALUES ($version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
