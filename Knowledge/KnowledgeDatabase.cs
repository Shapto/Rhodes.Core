using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Rhodes.Core.Documents;

namespace Rhodes.Core.Knowledge;

/// <summary>
/// The knowledge library's index (Data/knowledge.db): which files are indexed and when they last changed,
/// and their chunks in an FTS5 keyword index ranked with BM25. The files themselves stay in the Knowledge folder;
/// exact lines are always read from there. Kept apart from memory.db: this is reference material, not things about Shapto.
/// </summary>
public sealed partial class KnowledgeDatabase : IDisposable
{
    private const int MaximumQueryTerms = 32;

    private readonly SqliteConnection connection;
    private readonly object databaseLock = new();
    private readonly object lastReadFileLock = new();

    /// <summary>
    /// The last file read_knowledge read, so paging through a big archive or PDF doesn't unpack it again for every call.
    /// </summary>
    private (string FullPath, long LastWriteTicks, string[] Lines)? lastReadFile;

    public KnowledgeDatabase(string databasePath, string knowledgeFolder)
    {
        KnowledgeFolder = Path.GetFullPath(knowledgeFolder);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(null, "PRAGMA journal_mode = WAL;");
        Execute(null, "PRAGMA busy_timeout = 5000;");

        // words holds the parts of identifiers ("ShinVariant" adds "shin variant"), so a partial name still finds the code.
        Execute(null, """
        CREATE TABLE IF NOT EXISTS files (
            relative_path     TEXT PRIMARY KEY,
            last_write_ticks  INTEGER NOT NULL,
            size_bytes        INTEGER NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING fts5(
            relative_path UNINDEXED,
            collection UNINDEXED,
            title,
            content,
            start_line UNINDEXED,
            end_line UNINDEXED,
            words,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """);
    }

    /// <summary>
    /// The full path of RhodesIsland/Knowledge; every relative path in the index is relative to it.
    /// </summary>
    public string KnowledgeFolder { get; }

    /// <summary>
    /// Every indexed file with the change stamp it had when indexed, to tell which files need indexing again.
    /// </summary>
    public Dictionary<string, (long LastWriteTicks, long SizeBytes)> GetIndexedFileStamps()
    {
        lock (databaseLock)
        {
            using var command = CreateCommand(null, "SELECT relative_path, last_write_ticks, size_bytes FROM files;");
            using var reader = command.ExecuteReader();
            var stamps = new Dictionary<string, (long LastWriteTicks, long SizeBytes)>(StringComparer.Ordinal);
            while (reader.Read())
            {
                stamps[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            }

            return stamps;
        }
    }

    /// <summary>
    /// Swaps a file's old chunks for new ones and records its stamp, all in one transaction.
    /// </summary>
    public void ReplaceFileChunks(string relativePath, string collection, long lastWriteTicks, long sizeBytes, IReadOnlyList<KnowledgeChunk> fileChunks)
    {
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "DELETE FROM chunks WHERE relative_path = $path;", ("$path", relativePath));

            using var insertCommand = CreateCommand(transaction, "INSERT INTO chunks (relative_path, collection, title, content, start_line, end_line, words) VALUES ($path, $collection, $title, $content, $startLine, $endLine, $words);",
                ("$path", relativePath),
                ("$collection", collection),
                ("$title", ""),
                ("$content", ""),
                ("$startLine", 0),
                ("$endLine", 0),
                ("$words", ""));
            foreach (var chunk in fileChunks)
            {
                insertCommand.Parameters["$title"].Value = chunk.Title;
                insertCommand.Parameters["$content"].Value = chunk.Content;
                insertCommand.Parameters["$startLine"].Value = chunk.StartLine;
                insertCommand.Parameters["$endLine"].Value = chunk.EndLine;
                insertCommand.Parameters["$words"].Value = DocumentStore.SplitIdentifiers(chunk.Title + "\n" + chunk.Content);
                insertCommand.ExecuteNonQuery();
            }

            Execute(transaction, "INSERT INTO files (relative_path, last_write_ticks, size_bytes) VALUES ($path, $ticks, $size) ON CONFLICT(relative_path) DO UPDATE SET last_write_ticks = excluded.last_write_ticks, size_bytes = excluded.size_bytes;",
                ("$path", relativePath),
                ("$ticks", lastWriteTicks),
                ("$size", sizeBytes));
            transaction.Commit();
        }
    }

    /// <summary>
    /// Forgets a file that was deleted from the Knowledge folder.
    /// </summary>
    public void RemoveFile(string relativePath)
    {
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "DELETE FROM chunks WHERE relative_path = $path;", ("$path", relativePath));
            Execute(transaction, "DELETE FROM files WHERE relative_path = $path;", ("$path", relativePath));
            transaction.Commit();
        }
    }

    /// <summary>
    /// The best-matching chunks, best first. Titles count four times as much as content; identifier parts count least.
    /// With a collection, only that collection is searched.
    /// </summary>
    public IReadOnlyList<KnowledgeSearchResult> Search(string query, string? collection, int limit)
    {
        var matchExpression = BuildMatchExpression(query);
        if (matchExpression is null)
        {
            return [];
        }

        var parameters = new List<(string Name, object Value)> { ("$match", matchExpression), ("$limit", limit) };
        if (collection is not null)
        {
            parameters.Add(("$collection", collection));
        }

        lock (databaseLock)
        {
            using var command = CreateCommand(null, $"""
                SELECT relative_path, collection, title, start_line, end_line,
                       snippet(chunks, 3, '[', ']', '…', 32) AS snippet,
                       bm25(chunks, 0.0, 0.0, 4.0, 1.0, 0.0, 0.0, 0.5) AS score
                FROM chunks
                WHERE chunks MATCH $match {(collection is null ? "" : "AND collection = $collection COLLATE NOCASE")}
                ORDER BY score
                LIMIT $limit;
                """,
                [.. parameters]);

            using var reader = command.ExecuteReader();
            var results = new List<KnowledgeSearchResult>();
            while (reader.Read())
            {
                results.Add(new KnowledgeSearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5), -reader.GetDouble(6)));
            }

            return results;
        }
    }

    /// <summary>
    /// The collections that have at least one indexed file, with their file counts, sorted by name.
    /// </summary>
    public IReadOnlyList<(string Collection, int FileCount)> ListCollections()
    {
        // From the small files table, not the chunks: grouping the full-text table would scan every chunk on every search.
        return GetIndexedFileStamps().Keys
            .GroupBy(KnowledgeIndexer.GetCollection, StringComparer.OrdinalIgnoreCase)
            .Select(collectionGroup => (collectionGroup.Key, collectionGroup.Count()))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// How many files and chunks are indexed, for the startup report.
    /// </summary>
    public (int FileCount, int ChunkCount) CountIndexed()
    {
        lock (databaseLock)
        {
            using var fileCommand = CreateCommand(null, "SELECT COUNT(*) FROM files;");
            using var chunkCommand = CreateCommand(null, "SELECT COUNT(*) FROM chunks;");
            return (Convert.ToInt32(fileCommand.ExecuteScalar()), Convert.ToInt32(chunkCommand.ExecuteScalar()));
        }
    }

    /// <summary>
    /// Exact lines of an indexed file, 1-based and inclusive, read from disk. Null when the path isn't in the index
    /// (only indexed files can be read, so nothing outside the Knowledge folder is reachable) or the file is gone.
    /// </summary>
    public KnowledgeLines? ReadLines(string relativePath, int startLine, int endLine)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        bool isIndexed;
        lock (databaseLock)
        {
            using var command = CreateCommand(null, "SELECT COUNT(*) FROM files WHERE relative_path = $path;", ("$path", normalizedPath));
            isIndexed = Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        var fullPath = Path.GetFullPath(Path.Combine(KnowledgeFolder, normalizedPath));
        if (!isIndexed || !fullPath.StartsWith(KnowledgeFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath))
        {
            return null;
        }

        var allLines = ReadAllLinesCached(fullPath);
        if (allLines.Length == 0)
        {
            return new KnowledgeLines(normalizedPath, 1, 0, []);
        }

        var firstIndex = Math.Clamp(startLine, 1, allLines.Length) - 1;
        var lastIndex = Math.Clamp(endLine, firstIndex + 1, allLines.Length) - 1;
        return new KnowledgeLines(normalizedPath, firstIndex + 1, allLines.Length, allLines[firstIndex..(lastIndex + 1)]);
    }

    /// <summary>
    /// The file's lines as TextExtractor reads them, reusing the last result while the file hasn't changed.
    /// </summary>
    private string[] ReadAllLinesCached(string fullPath)
    {
        var lastWriteTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        lock (lastReadFileLock)
        {
            if (lastReadFile is { } cachedFile && cachedFile.FullPath == fullPath && cachedFile.LastWriteTicks == lastWriteTicks)
            {
                return cachedFile.Lines;
            }

            var lines = KnowledgeChunker.SplitLines(TextExtractor.ReadText(fullPath) ?? "");
            lastReadFile = (fullPath, lastWriteTicks, lines);
            return lines;
        }
    }

    /// <summary>
    /// Forward slashes and no leading "./" or "/", the form paths are stored in.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Trim().Replace('\\', '/').TrimStart('.', '/');
    }

    /// <summary>
    /// Every word of the query (letters, digits, underscores), quoted, joined with OR; identifiers also add their parts.
    /// Quoting keeps FTS5 syntax characters in the query from breaking the search.
    /// </summary>
    private static string? BuildMatchExpression(string query)
    {
        var queryWords = QueryWordPattern().Matches(query)
            .Select(wordMatch => wordMatch.Value)
            .Concat(DocumentStore.SplitIdentifiers(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(word => word.ToLowerInvariant())
            .Where(word => word.Length > 1)
            .Distinct()
            .Take(MaximumQueryTerms)
            .ToList();
        // A dotted name ("AccessTools.Method", "Mathf.Clamp") is also searched as a phrase, so chunks where the two parts
        // sit together (that member's own docs) rank above chunks that merely mention both words somewhere.
        var dottedNamePhrases = DottedNamePattern().Matches(query)
            .Select(nameMatch => string.Join(' ', nameMatch.Value.ToLowerInvariant().Split('.')))
            .Distinct()
            .Select(phrase => "\"" + phrase + "\"");
        var searchTerms = dottedNamePhrases.Concat(queryWords.Select(word => "\"" + word + "\"")).ToList();
        return queryWords.Count == 0 ? null : string.Join(" OR ", searchTerms);
    }

    private void Execute(SqliteTransaction? transaction, string commandText, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(transaction, commandText, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(SqliteTransaction? transaction, string commandText, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex QueryWordPattern();

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)+")]
    private static partial Regex DottedNamePattern();
}
