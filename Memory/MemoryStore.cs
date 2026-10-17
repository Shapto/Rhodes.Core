using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rhodes.Core.Memory
{

    /// <summary>
    /// The memory types from the design doc, stored in the closure_memory_meta side table.
    /// </summary>
    public static class MemoryTypes
    {
        public const string Fact = "fact";
        public const string Preference = "preference";
        public const string Project = "project";
        public const string Event = "event";
        public const string Persona = "persona";
        public const string Archive = "archive";

        public static readonly IReadOnlySet<string> All = new HashSet<string> { Fact, Preference, Project, Event, Persona, Archive };
    }

    /// <summary>
    /// One memory: the original khtsly row plus Closure's type from the side table.
    /// </summary>
    public sealed record MemoryRecord(
        string Id,
        string Content,
        string Type,
        string LegacyCategory,
        IReadOnlyList<string> Tags,
        double Confidence,
        string Source,
        string Scope,
        string? Project,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset LastAccessedAt,
        int AccessCount,
        bool NeedsReview);

    /// <summary>
    /// A keyword search result. KeywordScore is the negated bm25 rank, so higher means a better match.
    /// </summary>
    public sealed record MemorySearchHit(MemoryRecord Memory, double KeywordScore);

    /// <summary>
    /// One row of the import breakdown: how many memories share an old category, source and review flag.
    /// </summary>
    public sealed record MemoryBreakdownRow(string Category, string Source, bool NeedsReview, long Count);

    /// <summary>
    /// Reads and writes memory.db in place. The khtsly memories table keeps its exact shape;
    /// everything Closure adds lives in closure_* side tables, and no triggers touch memories,
    /// so the old plugin can still open the file if we ever roll back.
    /// </summary>
    public sealed class MemoryStore : IDisposable
    {
        private const int MaximumContentLength = 4000;
        private const int MaximumTagsPerMemory = 10;
        private const int MaximumTagLength = 50;
        private const int MaximumQueryWords = 32;

        private const string MemoryColumns = """
        memories.id, memories.content, memories.category, memories.tags, memories.confidence,
        memories.source, memories.scope, memories.project, memories.created_at, memories.updated_at,
        memories.last_accessed_at, memories.access_count,
        closure_memory_meta.type, closure_memory_meta.needs_review
        """;

        private const string BackfillMetaSql = """
        INSERT OR IGNORE INTO closure_memory_meta (memory_id, type, needs_review, imported_category)
        SELECT id,
               CASE category
                   WHEN 'fact' THEN 'fact'
                   WHEN 'preference' THEN 'preference'
                   WHEN 'instruction' THEN 'preference'
                   WHEN 'project' THEN 'project'
                   WHEN 'context' THEN 'event'
                   ELSE 'fact'
               END,
               CASE WHEN category IN ('note', 'context', 'relationship') OR source = 'ai-extracted' THEN 1 ELSE 0 END,
               category
        FROM memories;
        """;

        private static readonly JsonSerializerOptions TagJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly HashSet<string> StopWords =
        [
            "the", "and", "or", "for", "to", "of", "in", "on", "at", "is", "are", "was", "were", "be", "been", "it", "its",
            "this", "that", "these", "those", "with", "from", "by", "as", "an", "if", "so", "but", "not", "no", "yes",
            "what", "how", "why", "when", "where", "who", "which", "do", "does", "did", "done", "can", "could", "would", "should", "will",
            "my", "your", "you", "me", "we", "us", "our", "they", "them", "their", "he", "she", "his", "her", "him", "im", "ive", "dont",
            "hey", "hi", "hello", "ok", "okay", "yeah", "yep", "thanks", "thank", "please", "lol",
            "first", "last", "new", "old", "ever", "never", "just", "really", "very", "much", "more", "most", "some", "any", "all",
            "now", "today", "then", "again", "still", "also", "too", "here", "there", "about", "like", "thing", "things", "stuff",
            "get", "got", "make", "made", "want", "need", "know", "think", "see", "look", "go", "going", "use", "using", "good", "bad",
        ];

        private readonly SqliteConnection connection;
        private readonly object databaseLock = new();

        /// <summary>
        /// Opens an existing memory.db. Never creates one: a wrong path throws instead of starting her memory empty.
        /// </summary>
        public MemoryStore(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString();

            connection = new SqliteConnection(connectionString);
            connection.Open();
            Execute(null, "PRAGMA journal_mode = WAL;");
            Execute(null, "PRAGMA busy_timeout = 5000;");
            EnsureMemoriesTableExists();
            EnsureSideTables();
            RebuildSearchIndex();
        }

        /// <summary>
        /// Fills in types for rows Closure hasn't seen yet and rebuilds the keyword index from scratch.
        /// Runs on startup, so rows added or deleted while the core was off are picked up.
        /// </summary>
        public void RebuildSearchIndex()
        {
            lock (databaseLock)
            {
                using var transaction = connection.BeginTransaction();
                Execute(transaction, BackfillMetaSql);
                Execute(transaction, "DELETE FROM closure_memory_meta WHERE memory_id NOT IN (SELECT id FROM memories);");
                Execute(transaction, "DELETE FROM closure_memory_search;");
                Execute(transaction, "INSERT INTO closure_memory_search (memory_id, content, tags) SELECT id, content, tags FROM memories;");
                transaction.Commit();
            }
        }

        /// <summary>
        /// Saves a new memory and returns its id. The legacy category is filled in too, so the row
        /// still makes sense to the old plugin after a rollback.
        /// </summary>
        public string Save(string content, string type, IEnumerable<string> tags, double confidence, string source)
        {
            if (!MemoryTypes.All.Contains(type))
            {
                throw new ArgumentException($"Unknown memory type '{type}'.", nameof(type));
            }

            var trimmedContent = content.Trim();
            var storedContent = trimmedContent.Length > MaximumContentLength ? trimmedContent[..MaximumContentLength] : trimmedContent;
            var cleanedTags = tags
                .Select(tag => tag.Trim().ToLowerInvariant())
                .Where(tag => tag.Length > 0)
                .Select(tag => tag.Length > MaximumTagLength ? tag[..MaximumTagLength] : tag)
                .Distinct()
                .Take(MaximumTagsPerMemory)
                .ToList();
            var tagsJson = JsonSerializer.Serialize(cleanedTags, TagJsonOptions);
            var legacyCategory = type switch
            {
                MemoryTypes.Event => "context",
                MemoryTypes.Persona or MemoryTypes.Archive => "note",
                _ => type,
            };
            var memoryId = GenerateMemoryId();
            var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (databaseLock)
            {
                using var transaction = connection.BeginTransaction();
                Execute(transaction, """
                INSERT INTO memories (id, content, category, tags, confidence, source, scope, project,
                                      created_at, updated_at, last_accessed_at, access_count, supersedes)
                VALUES ($id, $content, $category, $tags, $confidence, $source, 'global', NULL,
                        $now, $now, $now, 0, NULL);
                """,
                    ("$id", memoryId),
                    ("$content", storedContent),
                    ("$category", legacyCategory),
                    ("$tags", tagsJson),
                    ("$confidence", Math.Clamp(confidence, 0.0, 1.0)),
                    ("$source", source),
                    ("$now", nowMilliseconds));
                Execute(transaction, "INSERT INTO closure_memory_meta (memory_id, type, needs_review) VALUES ($id, $type, 0);",
                    ("$id", memoryId),
                    ("$type", type));
                Execute(transaction, "INSERT INTO closure_memory_search (memory_id, content, tags) VALUES ($id, $content, $tags);",
                    ("$id", memoryId),
                    ("$content", storedContent),
                    ("$tags", tagsJson));
                transaction.Commit();
            }

            return memoryId;
        }

        /// <summary>
        /// Deletes a memory everywhere. "Forget that" means this, not marking it as old.
        /// </summary>
        public bool Delete(string memoryId)
        {
            lock (databaseLock)
            {
                using var transaction = connection.BeginTransaction();
                var deletedRows = Execute(transaction, "DELETE FROM memories WHERE id = $id;", ("$id", memoryId));
                Execute(transaction, "DELETE FROM closure_memory_meta WHERE memory_id = $id;", ("$id", memoryId));
                Execute(transaction, "DELETE FROM closure_memory_search WHERE memory_id = $id;", ("$id", memoryId));
                transaction.Commit();
                return deletedRows > 0;
            }
        }

        /// <summary>
        /// Keyword search over content and tags. Archive memories are left out unless asked for,
        /// so old system prompts never get auto-injected.
        /// </summary>
        public IReadOnlyList<MemorySearchHit> Search(string query, int limit, bool includeArchive)
        {
            var matchExpression = BuildMatchExpression(query);
            if (matchExpression is null)
            {
                return [];
            }

            lock (databaseLock)
            {
                using var command = CreateCommand(null,
                    "SELECT " + MemoryColumns + " " + """
                , bm25(closure_memory_search)
                FROM closure_memory_search
                JOIN memories ON memories.id = closure_memory_search.memory_id
                JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
                WHERE closure_memory_search MATCH $matchExpression
                  AND ($includeArchive = 1 OR closure_memory_meta.type <> 'archive')
                ORDER BY bm25(closure_memory_search)
                LIMIT $limit;
                """,
                    ("$matchExpression", matchExpression),
                    ("$includeArchive", includeArchive ? 1 : 0),
                    ("$limit", limit));

                using var reader = command.ExecuteReader();
                var hits = new List<MemorySearchHit>();
                while (reader.Read())
                {
                    hits.Add(new MemorySearchHit(ReadMemory(reader), -reader.GetDouble(14)));
                }

                return hits;
            }
        }

        /// <summary>
        /// The most recently created memories, newest first (Search Memory's "recent" mode).
        /// </summary>
        public IReadOnlyList<MemoryRecord> GetRecent(int limit)
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null,
                "SELECT " + MemoryColumns + " " + """
                FROM memories
                JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
                ORDER BY memories.created_at DESC
                LIMIT $limit;
                """,
                ("$limit", limit));

                return ReadAllMemories(command);
            }
        }

        /// <summary>
        /// Records that memories were used. Call it for deliberate searches only, not auto-inject,
        /// same as the old plugin, so injection doesn't inflate the counts.
        /// </summary>
        public void TouchAccess(IEnumerable<string> memoryIds)
        {
            var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (databaseLock)
            {
                using var transaction = connection.BeginTransaction();
                foreach (var memoryId in memoryIds)
                {
                    Execute(transaction, "UPDATE memories SET last_accessed_at = $now, access_count = access_count + 1 WHERE id = $id;",
                        ("$now", nowMilliseconds),
                        ("$id", memoryId));
                }

                transaction.Commit();
            }
        }

        /// <summary>
        /// Copies the live database with SQLite's backup API (safe while in use and in WAL mode),
        /// then deletes all but the newest copiesToKeep backups.
        /// </summary>
        public string CreateBackup(string backupFolder, int copiesToKeep)
        {
            Directory.CreateDirectory(backupFolder);
            var backupPath = Path.Combine(backupFolder, $"memory-{DateTime.Now:yyyy-MM-dd_HHmmss}.db");
            var backupConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Pooling = false,
            }.ToString();

            lock (databaseLock)
            {
                using var backupConnection = new SqliteConnection(backupConnectionString);
                backupConnection.Open();
                connection.BackupDatabase(backupConnection);
            }

            var oldBackups = new DirectoryInfo(backupFolder)
                .GetFiles("memory-*.db")
                .OrderByDescending(file => file.Name)
                .Skip(copiesToKeep);
            foreach (var oldBackup in oldBackups)
            {
                oldBackup.Delete();
            }

            return backupPath;
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        private void EnsureMemoriesTableExists()
        {
            using var command = CreateCommand(null, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'memories';");
            var tableCount = Convert.ToInt64(command.ExecuteScalar());
            if (tableCount == 0)
            {
                throw new InvalidOperationException("No memories table in this file. Check that the path points at the khtsly memory.db.");
            }
        }

        /// <summary>
        /// Counts memories per old category, source and review flag, to see what the old plugin left behind.
        /// </summary>
        public IReadOnlyList<MemoryBreakdownRow> GetBreakdown()
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null, """
            SELECT memories.category, memories.source, closure_memory_meta.needs_review, COUNT(*)
            FROM memories
            JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
            GROUP BY memories.category, memories.source, closure_memory_meta.needs_review
            ORDER BY COUNT(*) DESC;
            """);

                using var reader = command.ExecuteReader();
                var breakdownRows = new List<MemoryBreakdownRow>();
                while (reader.Read())
                {
                    breakdownRows.Add(new MemoryBreakdownRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3)));
                }

                return breakdownRows;
            }
        }

        /// <summary>
        /// Every memory whose text contains the given words (case-insensitive), oldest first. For cleanup, not for her.
        /// </summary>
        public IReadOnlyList<MemoryRecord> FindContaining(string text, int limit)
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null,
                    "SELECT " + MemoryColumns + " " + """
            FROM memories
            JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
            WHERE memories.content LIKE '%' || $text || '%'
            ORDER BY memories.created_at
            LIMIT $limit;
            """,
                    ("$text", text),
                    ("$limit", limit));
                return ReadAllMemories(command);
            }
        }

        private void EnsureSideTables()
        {
            Execute(null, """
            CREATE TABLE IF NOT EXISTS closure_memory_meta (
                memory_id          TEXT PRIMARY KEY,
                type               TEXT NOT NULL,
                needs_review       INTEGER NOT NULL DEFAULT 0,
                imported_category  TEXT
            );
            """);
            Execute(null, "DROP TABLE IF EXISTS closure_memory_search;");
            Execute(null, """
            CREATE VIRTUAL TABLE closure_memory_search USING fts5(
                memory_id UNINDEXED,
                content,
                tags,
                tokenize = 'porter unicode61 remove_diacritics 2'
            );
            """);
        }

        /// <summary>
        /// The meaningful words in a text: lowercase, stop words removed, no duplicates.
        /// </summary>
        public static IReadOnlyList<string> GetSearchWords(string text)
        {
            return Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_]+")
                .Select(match => match.Value)
                .Where(word => word.Length >= 2 && !StopWords.Contains(word))
                .Distinct()
                .Take(MaximumQueryWords)
                .ToList();
        }

        private static string? BuildMatchExpression(string query)
        {
            var quotedWords = GetSearchWords(query)
                .Select(word => $"\"{word}\"")
                .ToList();
            return quotedWords.Count == 0 ? null : string.Join(" OR ", quotedWords);
        }

        private static MemoryRecord ReadMemory(SqliteDataReader reader)
        {
            return new MemoryRecord(
                Id: reader.GetString(0),
                Content: reader.GetString(1),
                LegacyCategory: reader.GetString(2),
                Tags: ParseTags(reader.GetString(3)),
                Confidence: reader.GetDouble(4),
                Source: reader.GetString(5),
                Scope: reader.GetString(6),
                Project: reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                LastAccessedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                AccessCount: reader.GetInt32(11),
                Type: reader.GetString(12),
                NeedsReview: reader.GetInt64(13) != 0);
        }

        private static List<MemoryRecord> ReadAllMemories(SqliteCommand command)
        {
            using var reader = command.ExecuteReader();
            var memories = new List<MemoryRecord>();
            while (reader.Read())
            {
                memories.Add(ReadMemory(reader));
            }

            return memories;
        }

        private static IReadOnlyList<string> ParseTags(string tagsJson)
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// Same id format as the khtsly plugin: 12 random bytes as base64url, 16 characters.
        /// </summary>
        private static string GenerateMemoryId()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
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

        /// <summary>
        /// Memories flagged on import because their old category didn't map cleanly to a type.
        /// </summary>
        public IReadOnlyList<MemoryRecord> GetNeedingReview()
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null,
                    "SELECT " + MemoryColumns + " " + """
            FROM memories
            JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
            WHERE closure_memory_meta.needs_review = 1
            ORDER BY memories.category, memories.created_at;
            """);
                return ReadAllMemories(command);
            }
        }

        /// <summary>
        /// Memories by the old plugin's category, e.g. "instruction", oldest first.
        /// </summary>
        public IReadOnlyList<MemoryRecord> GetByLegacyCategory(string category, int limit)
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null,
                    "SELECT " + MemoryColumns + " " + """
            FROM memories
            JOIN closure_memory_meta ON closure_memory_meta.memory_id = memories.id
            WHERE memories.category = $category
            ORDER BY memories.created_at
            LIMIT $limit;
            """,
                    ("$category", category),
                    ("$limit", limit));
                return ReadAllMemories(command);
            }
        }

        /// <summary>
        /// Changes a memory's type and clears its review flag. Only the side table changes;
        /// the original row stays exactly as the old plugin wrote it.
        /// </summary>
        public bool SetType(string memoryId, string type)
        {
            if (!MemoryTypes.All.Contains(type))
            {
                throw new ArgumentException($"Unknown memory type '{type}'.", nameof(type));
            }

            lock (databaseLock)
            {
                var updatedRows = Execute(null, "UPDATE closure_memory_meta SET type = $type, needs_review = 0 WHERE memory_id = $id;",
                    ("$type", type),
                    ("$id", memoryId));
                return updatedRows > 0;
            }
        }

        /// <summary>
        /// The highest use count of any memory, so use counts can be scaled against it.
        /// </summary>
        public long GetMaximumAccessCount()
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(null, "SELECT COALESCE(MAX(access_count), 0) FROM memories;");
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private int Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(transaction, sql, parameters);
            return command.ExecuteNonQuery();
        }
    }
}
