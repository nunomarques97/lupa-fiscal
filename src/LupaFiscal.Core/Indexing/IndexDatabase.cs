using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LupaFiscal.Core.Indexing;

/// <summary>
/// Ruling metadata and normalised body as stored in the index. Tax, article and source URL are those
/// of its display (canonical) listing; <paramref name="PdfSha256"/> is the content identity used to
/// merge byte-identical PDFs listed by several taxes.
/// </summary>
public sealed record IndexedRuling(
    string Id,
    string Tax,
    string Diploma,
    string Article,
    string Paragraph,
    DateOnly? PublishedOn,
    DateOnly? DecisionDate,
    string ProcessNumber,
    string Subject,
    string SourceUrl,
    string Body,
    string? PdfSha256 = null)
{
    /// <summary>Year used by the year filter: the publication year (always present in the listing).</summary>
    public int? Year => PublishedOn?.Year;
}

/// <summary>
/// One entry of a tax listing, resolved to the ruling it is stored as: <paramref name="Id"/> is the
/// corpus id of the entry, <paramref name="RulingId"/> is that same id or, for a PDF already listed by
/// an earlier tax, the id of that canonical ruling. The tax and article filters match listings.
/// </summary>
public sealed record IndexedListing(string Id, string RulingId, string Tax, string Article);

/// <summary>Index state of one stored ruling: its display tax, chunk key, chunks and chunks with a vector.</summary>
public sealed record RulingIndexState(string Tax, string? ChunkKey, int Chunks, int Vectors);

/// <summary>A chunk ready to be written, with its embedding.</summary>
public sealed record ChunkRow(Chunk Chunk, float[] Vector);

/// <summary>
/// The SQLite index (data/lupa-fiscal.db): rulings, the tax listings that resolve to them, their
/// chunks with float32 vector BLOBs, and an external-content FTS5 table over the chunk text
/// (unicode61, diacritics removed) kept in sync by triggers. Every statement is parameterised.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    /// <summary>Version 2 adds listings and the PDF hash (v0.2); a version 1 index is migrated in place.</summary>
    public const int SchemaVersion = 2;

    private const string ListingsTable = """
        CREATE TABLE IF NOT EXISTS listings (
            id TEXT PRIMARY KEY,
            ruling_id TEXT NOT NULL REFERENCES rulings (id) ON DELETE CASCADE,
            tax TEXT NOT NULL,
            article TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS listings_ruling ON listings (ruling_id);
        CREATE INDEX IF NOT EXISTS listings_tax_article ON listings (tax, article);
        """;

    private readonly SqliteConnection _connection;

    private IndexDatabase(SqliteConnection connection) => _connection = connection;

    public static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        // No pooling: the file is released as soon as the connection is disposed.
        Pooling = false,
    }.ToString();

    /// <summary>Opens (creating or migrating if needed) the index for writing and ensures the schema.</summary>
    public static IndexDatabase OpenForWrite(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(ConnectionString(path, readOnly: false));
        connection.Open();
        var database = new IndexDatabase(connection);
        database.EnsureSchema();
        return database;
    }

    /// <summary>Opens an existing index read-only. Throws when it does not exist or has another schema.</summary>
    public static IndexDatabase OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"No index at {path}. Run: index", path);
        var connection = new SqliteConnection(ConnectionString(path, readOnly: true));
        connection.Open();
        var database = new IndexDatabase(connection);
        var version = database.GetMeta("schema_version");
        if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            database.Dispose();
            throw new InvalidDataException($"Index {path} has schema version {version ?? "none"}, expected {SchemaVersion}. Run: index");
        }
        return database;
    }

    internal SqliteConnection Connection => _connection;

    private void EnsureSchema()
    {
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA foreign_keys = ON;");
        var version = TableExists("meta") ? GetMeta("schema_version") : null;
        if (version == "1")
        {
            MigrateFromVersion1();
            version = SchemaVersion.ToString(CultureInfo.InvariantCulture);
        }
        if (version is not null && version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException($"Index has schema version {version}, expected {SchemaVersion}; delete the index file and run index again.");
        }

        Execute("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rulings (
                id TEXT PRIMARY KEY,
                tax TEXT NOT NULL,
                diploma TEXT NOT NULL,
                article TEXT NOT NULL,
                paragraph TEXT NOT NULL,
                published_on TEXT,
                decision_date TEXT,
                year INTEGER,
                process_number TEXT NOT NULL,
                subject TEXT NOT NULL,
                source_url TEXT NOT NULL,
                body TEXT NOT NULL,
                chunk_key TEXT,
                pdf_sha256 TEXT
            );
            CREATE INDEX IF NOT EXISTS rulings_tax ON rulings (tax);
            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY,
                ruling_id TEXT NOT NULL REFERENCES rulings (id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                section TEXT NOT NULL,
                start_offset INTEGER NOT NULL,
                end_offset INTEGER NOT NULL,
                text TEXT NOT NULL,
                token_count INTEGER NOT NULL,
                vector BLOB,
                UNIQUE (ruling_id, ordinal)
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5 (
                text, content = 'chunks', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2'
            );
            CREATE TRIGGER IF NOT EXISTS chunks_after_insert AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts (rowid, text) VALUES (new.id, new.text);
            END;
            CREATE TRIGGER IF NOT EXISTS chunks_after_delete AFTER DELETE ON chunks BEGIN
                INSERT INTO chunks_fts (chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
            END;
            CREATE TRIGGER IF NOT EXISTS chunks_after_update AFTER UPDATE OF text ON chunks BEGIN
                INSERT INTO chunks_fts (chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
                INSERT INTO chunks_fts (rowid, text) VALUES (new.id, new.text);
            END;
            """);
        Execute(ListingsTable);
        SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Version 1 (v0.1: one tax, no listings) to 2 in one transaction: every ruling becomes its own
    /// single listing. Chunks, vectors and chunk keys are untouched, so nothing is embedded again.
    /// </summary>
    private void MigrateFromVersion1()
    {
        using var transaction = BeginTransaction();
        foreach (var sql in new[]
                 {
                     "ALTER TABLE rulings ADD COLUMN pdf_sha256 TEXT;",
                     ListingsTable,
                     "INSERT INTO listings (id, ruling_id, tax, article) SELECT id, id, tax, article FROM rulings;",
                     "UPDATE meta SET value = '2' WHERE key = 'schema_version';",
                 })
        {
            using var command = Command(sql);
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string? GetMeta(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE key = $key", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var command = Command(
            "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
        command.ExecuteNonQuery();
    }

    /// <summary>State of every indexed ruling: its display tax, chunk key and whether all its chunks have a vector of the right size.</summary>
    public Dictionary<string, RulingIndexState> RulingStates(int dimensions)
    {
        using var command = Command("""
            SELECT r.id, r.tax, r.chunk_key, COUNT(c.id), COALESCE(SUM(length(c.vector) = $bytes), 0)
            FROM rulings r LEFT JOIN chunks c ON c.ruling_id = r.id
            GROUP BY r.id
            """, ("$bytes", dimensions * sizeof(float)));
        using var reader = command.ExecuteReader();
        var states = new Dictionary<string, RulingIndexState>(StringComparer.Ordinal);
        while (reader.Read())
        {
            states[reader.GetString(0)] = new RulingIndexState(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4));
        }
        return states;
    }

    /// <summary>Every listing in the index, by listing id.</summary>
    public Dictionary<string, IndexedListing> Listings()
    {
        using var command = Command("SELECT id, ruling_id, tax, article FROM listings");
        using var reader = command.ExecuteReader();
        var listings = new Dictionary<string, IndexedListing>(StringComparer.Ordinal);
        while (reader.Read())
        {
            listings[reader.GetString(0)] = new IndexedListing(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        return listings;
    }

    public void UpsertRuling(IndexedRuling ruling, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO rulings (id, tax, diploma, article, paragraph, published_on, decision_date, year,
                                 process_number, subject, source_url, body, pdf_sha256)
            VALUES ($id, $tax, $diploma, $article, $paragraph, $published, $decision, $year,
                    $process, $subject, $url, $body, $sha)
            ON CONFLICT (id) DO UPDATE SET
                tax = excluded.tax, diploma = excluded.diploma, article = excluded.article,
                paragraph = excluded.paragraph, published_on = excluded.published_on,
                decision_date = excluded.decision_date, year = excluded.year,
                process_number = excluded.process_number, subject = excluded.subject,
                source_url = excluded.source_url, body = excluded.body, pdf_sha256 = excluded.pdf_sha256
            """,
            ("$id", ruling.Id), ("$tax", ruling.Tax), ("$diploma", ruling.Diploma), ("$article", ruling.Article),
            ("$paragraph", ruling.Paragraph), ("$published", Iso(ruling.PublishedOn)), ("$decision", Iso(ruling.DecisionDate)),
            ("$year", ruling.Year), ("$process", ruling.ProcessNumber), ("$subject", ruling.Subject),
            ("$url", ruling.SourceUrl), ("$body", ruling.Body), ("$sha", ruling.PdfSha256));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    /// <summary>Adds or repoints a listing; its ruling must already be stored.</summary>
    public void UpsertListing(IndexedListing listing, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO listings (id, ruling_id, tax, article) VALUES ($id, $ruling, $tax, $article)
            ON CONFLICT (id) DO UPDATE SET ruling_id = excluded.ruling_id, tax = excluded.tax, article = excluded.article
            """, ("$id", listing.Id), ("$ruling", listing.RulingId), ("$tax", listing.Tax), ("$article", listing.Article));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public void DeleteListing(string listingId, SqliteTransaction transaction)
    {
        using var command = Command("DELETE FROM listings WHERE id = $id", ("$id", listingId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    /// <summary>Replaces every chunk of a ruling and records the key they were built from.</summary>
    public void ReplaceChunks(string rulingId, string chunkKey, IReadOnlyList<ChunkRow> rows, SqliteTransaction transaction)
    {
        using (var delete = Command("DELETE FROM chunks WHERE ruling_id = $id", ("$id", rulingId)))
        {
            delete.Transaction = transaction;
            delete.ExecuteNonQuery();
        }

        using var insert = Command("""
            INSERT INTO chunks (ruling_id, ordinal, section, start_offset, end_offset, text, token_count, vector)
            VALUES ($ruling, $ordinal, $section, $start, $end, $text, $tokens, $vector)
            """);
        insert.Transaction = transaction;
        var ruling = insert.Parameters.Add("$ruling", SqliteType.Text);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var section = insert.Parameters.Add("$section", SqliteType.Text);
        var start = insert.Parameters.Add("$start", SqliteType.Integer);
        var end = insert.Parameters.Add("$end", SqliteType.Integer);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        var tokens = insert.Parameters.Add("$tokens", SqliteType.Integer);
        var vector = insert.Parameters.Add("$vector", SqliteType.Blob);
        foreach (var row in rows)
        {
            ruling.Value = rulingId;
            ordinal.Value = row.Chunk.Ordinal;
            section.Value = row.Chunk.Section;
            start.Value = row.Chunk.Start;
            end.Value = row.Chunk.End;
            text.Value = row.Chunk.Text;
            tokens.Value = row.Chunk.TokenCount;
            vector.Value = Embeddings.VectorMath.ToBytes(row.Vector);
            insert.ExecuteNonQuery();
        }

        using var key = Command("UPDATE rulings SET chunk_key = $key WHERE id = $id", ("$key", chunkKey), ("$id", rulingId));
        key.Transaction = transaction;
        key.ExecuteNonQuery();
    }

    /// <summary>Removes a ruling with its chunks and every listing that resolves to it.</summary>
    public void DeleteRuling(string rulingId, SqliteTransaction transaction)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM chunks WHERE ruling_id = $id",
                     "DELETE FROM listings WHERE ruling_id = $id",
                     "DELETE FROM rulings WHERE id = $id",
                 })
        {
            using var command = Command(sql, ("$id", rulingId));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
    }

    public SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    /// <summary>Total chunks, and chunks whose vector BLOB has the expected size.</summary>
    public (int Rulings, int Chunks, int Vectors) Counts(int dimensions)
    {
        using var command = Command("""
            SELECT (SELECT COUNT(*) FROM rulings), COUNT(*), COALESCE(SUM(length(vector) = $bytes), 0) FROM chunks
            """, ("$bytes", dimensions * sizeof(float)));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Every ruling id in the index.</summary>
    public HashSet<string> RulingIds()
    {
        using var command = Command("SELECT id FROM rulings");
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public void Optimize()
    {
        Execute("INSERT INTO chunks_fts (chunks_fts) VALUES ('optimize');");
        Execute("PRAGMA optimize;");
    }

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private bool TableExists(string name)
    {
        using var command = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name", ("$name", name));
        return command.ExecuteScalar() is not null;
    }

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose() => _connection.Dispose();
}
