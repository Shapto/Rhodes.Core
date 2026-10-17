using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Rhodes.Core.Memory;

namespace Rhodes.Core.Documents;

public sealed record DocumentRecord(string Id, string Name, string Source, int Characters, int LineCount, DateTimeOffset CreatedAt);

public sealed record DocumentChunk(int StartLine, int EndLine, string Text, double Score);

public sealed record DocumentLines(DocumentRecord Document, int StartLine, IReadOnlyList<string> Lines);

/// <summary>
/// Big texts Closure looks things up in instead of reading whole: attached files that don't fit, and long pages or files she fetched.
/// The full text is kept in Data/documents/{id}.txt for reading exact lines; rhodes.db holds it in chunks in a keyword index.
/// </summary>
public sealed partial class DocumentStore : IDisposable
{
    public const int ChunkCharacters = 1500;
    public const double TokensPerCharacter = 0.3;
    private const int MaximumQueryTerms = 32;

    private readonly SqliteConnection connection;
    private readonly object databaseLock = new();
    private readonly string documentsFolder;

    public DocumentStore(string databasePath, string documentsFolder)
    {
        this.documentsFolder = documentsFolder;
        Directory.CreateDirectory(documentsFolder);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(null, "PRAGMA journal_mode = WAL;");
        Execute(null, "PRAGMA busy_timeout = 5000;");
        Execute(null, """
        CREATE TABLE IF NOT EXISTS documents (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            source      TEXT NOT NULL,
            characters  INTEGER NOT NULL,
            line_count  INTEGER NOT NULL,
            created_at  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS documents_by_source ON documents (source, created_at);
        CREATE VIRTUAL TABLE IF NOT EXISTS document_chunks USING fts5(
            document_id UNINDEXED,
            start_line UNINDEXED,
            end_line UNINDEXED,
            text,
            words,
            tokenize = 'porter unicode61 remove_diacritics 2'
        );
        """);
    }

    public static bool IsValidId(string? documentId)
    {
        return documentId is not null && DocumentIdPattern().IsMatch(documentId);
    }

    public static string NewId()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }

    public DocumentRecord? Find(string documentId)
    {
        if (!IsValidId(documentId))
        {
            return null;
        }

        lock (databaseLock)
        {
            using var command = CreateCommand(null, "SELECT id, name, source, characters, line_count, created_at FROM documents WHERE id = $id;", ("$id", documentId));
            return ReadDocuments(command).FirstOrDefault();
        }
    }

    /// <summary>
    /// The newest document made from this source (a URL) within maximumAge, so fetching the same page again doesn't download it twice.
    /// </summary>
    public DocumentRecord? FindBySource(string source, TimeSpan maximumAge)
    {
        var oldestAllowedMilliseconds = DateTimeOffset.UtcNow.Subtract(maximumAge).ToUnixTimeMilliseconds();
        lock (databaseLock)
        {
            using var command = CreateCommand(null, "SELECT id, name, source, characters, line_count, created_at FROM documents WHERE source = $source AND created_at >= $oldest ORDER BY created_at DESC LIMIT 1;",
                ("$source", source),
                ("$oldest", oldestAllowedMilliseconds));
            return ReadDocuments(command).FirstOrDefault();
        }
    }

    /// <summary>
    /// Saves a text and indexes it in chunks. Uploads pass their upload id so the same file is only indexed once; fetched pages pass null.
    /// </summary>
    public DocumentRecord Add(string? documentId, string name, string source, string text)
    {
        var id = documentId ?? NewId();
        if (!IsValidId(id))
        {
            throw new ArgumentException("Document ids are 12 hex characters.", nameof(documentId));
        }

        var existingDocument = Find(id);
        if (existingDocument is not null)
        {
            return existingDocument;
        }

        var lines = SplitLines(text);
        File.WriteAllText(Path.Combine(documentsFolder, id + ".txt"), text, Encoding.UTF8);
        var record = new DocumentRecord(id, name, source, text.Length, lines.Length, DateTimeOffset.Now);

        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "INSERT INTO documents (id, name, source, characters, line_count, created_at) VALUES ($id, $name, $source, $characters, $lineCount, $createdAt);",
                ("$id", id),
                ("$name", name),
                ("$source", source),
                ("$characters", text.Length),
                ("$lineCount", lines.Length),
                ("$createdAt", record.CreatedAt.ToUnixTimeMilliseconds()));

            using var insertCommand = CreateCommand(transaction, "INSERT INTO document_chunks (document_id, start_line, end_line, text, words) VALUES ($id, $startLine, $endLine, $text, $words);",
                ("$id", id),
                ("$startLine", 0),
                ("$endLine", 0),
                ("$text", ""),
                ("$words", ""));
            foreach (var (startLine, endLine, chunkText) in SplitIntoChunks(lines))
            {
                insertCommand.Parameters["$startLine"].Value = startLine;
                insertCommand.Parameters["$endLine"].Value = endLine;
                insertCommand.Parameters["$text"].Value = chunkText;
                insertCommand.Parameters["$words"].Value = SplitIdentifiers(chunkText);
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return record;
    }

    /// <summary>
    /// The chunks that best match the query's words, best first. Words inside identifiers count too ("variant" finds ShinVariant).
    /// </summary>
    public IReadOnlyList<DocumentChunk> Search(string documentId, string query, int limit)
    {
        var matchExpression = BuildMatchExpression(query);
        if (matchExpression is null || !IsValidId(documentId))
        {
            return [];
        }

        lock (databaseLock)
        {
            using var command = CreateCommand(null, """
                SELECT start_line, end_line, text, bm25(document_chunks, 0.0, 0.0, 0.0, 1.0, 0.6) AS score
                FROM document_chunks
                WHERE document_chunks MATCH $match AND document_id = $id
                ORDER BY score
                LIMIT $limit;
                """,
                ("$match", matchExpression),
                ("$id", documentId),
                ("$limit", limit));

            using var reader = command.ExecuteReader();
            var chunks = new List<DocumentChunk>();
            while (reader.Read())
            {
                chunks.Add(new DocumentChunk(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), -reader.GetDouble(3)));
            }

            return chunks;
        }
    }

    /// <summary>
    /// Exact lines, 1-based. Returns null when the document doesn't exist or its text file is gone.
    /// </summary>
    public DocumentLines? ReadLines(string documentId, int startLine, int lineCount)
    {
        var document = Find(documentId);
        var textPath = Path.Combine(documentsFolder, documentId + ".txt");
        if (document is null || !File.Exists(textPath))
        {
            return null;
        }

        var lines = File.ReadLines(textPath, Encoding.UTF8)
            .Skip(Math.Max(0, startLine - 1))
            .Take(lineCount)
            .ToList();
        return new DocumentLines(document, startLine, lines);
    }

    public string? ReadAllText(string documentId)
    {
        var textPath = Path.Combine(documentsFolder, documentId + ".txt");
        return IsValidId(documentId) && File.Exists(textPath) ? File.ReadAllText(textPath, Encoding.UTF8) : null;
    }

    public static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Lines with their numbers in front ("  412 | text"), so she can quote them and ask for more by number.
    /// </summary>
    public static string NumberLines(int firstLineNumber, IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        var lineNumber = firstLineNumber;
        foreach (var line in lines)
        {
            builder.Append(lineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append(" | ").Append(line.TrimEnd('\r')).Append('\n');
            lineNumber++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Line-aligned chunks of about ChunkCharacters each, so every chunk knows its line range.
    /// A single very long line (minified code, a JSON blob) is cut into pieces of its own.
    /// </summary>
    private static List<(int StartLine, int EndLine, string Text)> SplitIntoChunks(string[] lines)
    {
        var chunks = new List<(int StartLine, int EndLine, string Text)>();
        var builder = new StringBuilder();
        var chunkStartLine = 1;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            var line = lines[lineIndex];
            if (line.Length > ChunkCharacters)
            {
                if (builder.Length > 0)
                {
                    chunks.Add((chunkStartLine, lineNumber - 1, builder.ToString()));
                    builder.Clear();
                }

                for (var pieceStart = 0; pieceStart < line.Length; pieceStart += ChunkCharacters)
                {
                    chunks.Add((lineNumber, lineNumber, line.Substring(pieceStart, Math.Min(ChunkCharacters, line.Length - pieceStart))));
                }

                chunkStartLine = lineNumber + 1;
                continue;
            }

            if (builder.Length > 0 && builder.Length + line.Length + 1 > ChunkCharacters)
            {
                chunks.Add((chunkStartLine, lineNumber - 1, builder.ToString()));
                builder.Clear();
                chunkStartLine = lineNumber;
            }

            builder.Append(line).Append('\n');
        }

        if (builder.Length > 0)
        {
            chunks.Add((chunkStartLine, lines.Length, builder.ToString()));
        }

        return chunks;
    }

    /// <summary>
    /// The parts of identifiers, as extra searchable words: "ShinVariant_Base" adds "shin variant base".
    /// </summary>
    private static string SplitIdentifiers(string text)
    {
        var parts = new List<string>();
        foreach (Match identifierMatch in IdentifierPattern().Matches(text))
        {
            var identifierParts = IdentifierBoundaryPattern().Split(identifierMatch.Value)
                .Where(part => part.Length > 1)
                .ToList();
            if (identifierParts.Count > 1)
            {
                parts.AddRange(identifierParts);
            }
        }

        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>
    /// Every meaningful word of the query, quoted, joined with OR; identifiers also add their parts.
    /// </summary>
    private static string? BuildMatchExpression(string query)
    {
        var terms = MemoryStore.GetSearchWords(query)
            .Concat(SplitIdentifiers(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(term => term.Replace("\"", "").ToLowerInvariant())
            .Where(term => term.Length > 1)
            .Distinct()
            .Take(MaximumQueryTerms)
            .ToList();
        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(term => "\"" + term + "\""));
    }

    private static List<DocumentRecord> ReadDocuments(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var documents = new List<DocumentRecord>();
        while (reader.Read())
        {
            documents.Add(new DocumentRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).ToLocalTime()));
        }

        return documents;
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    private SqliteCommand CreateCommand(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private int Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    [GeneratedRegex("^[a-f0-9]{12}$")]
    private static partial Regex DocumentIdPattern();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*[a-z][A-Z0-9_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"_+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex IdentifierBoundaryPattern();
}