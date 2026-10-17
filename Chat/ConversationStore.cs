using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Rhodes.Core.Chat;

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount, bool Compacted);

/// <summary>
/// One match from searching past chats: which chat, which message (its position, as read_chat takes it), who said it, and the matching bit.
/// </summary>
public sealed record ChatSearchHit(string ConversationId, string Title, int Position, string Role, string? SentAt, string Snippet);

/// <summary>
/// Saves conversations in rhodes.db, separate from memory.db: chat history is Rhodes' own data, memories are hers.
/// Messages are stored as the model sees them, plus Rhodes-only "rhodes_" fields that never go to the model.
/// </summary>
public sealed partial class ConversationStore : IDisposable
{
    private const int MaximumSearchTerms = 12;

    private readonly SqliteConnection connection;
    private readonly object databaseLock = new();

    public ConversationStore(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(null, "PRAGMA journal_mode = WAL;");
        Execute(null, """
        CREATE TABLE IF NOT EXISTS conversations (
            id                    TEXT PRIMARY KEY,
            title                 TEXT NOT NULL DEFAULT '',
            created_at            INTEGER NOT NULL,
            updated_at            INTEGER NOT NULL,
            last_user_message_at  INTEGER
        );
        CREATE TABLE IF NOT EXISTS conversation_messages (
            conversation_id  TEXT NOT NULL,
            position         INTEGER NOT NULL,
            message_json     TEXT NOT NULL,
            PRIMARY KEY (conversation_id, position)
        );
        """);
        AddColumnIfMissing("summary", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("summary_covers_through", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("tool_results_collapsed_before", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("title_is_manual", "INTEGER NOT NULL DEFAULT 0");

        // The search index holds what Shapto and Closure said (not tool results or thinking), one row per message.
        // Chats from before the index existed are indexed once, the first time the core starts with it.
        using var indexExistsCommand = CreateCommand(null, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'chat_search';");
        var indexIsNew = Convert.ToInt64(indexExistsCommand.ExecuteScalar()) == 0;
        Execute(null, """
        CREATE VIRTUAL TABLE IF NOT EXISTS chat_search USING fts5(
            conversation_id UNINDEXED,
            position UNINDEXED,
            role UNINDEXED,
            sent_at UNINDEXED,
            text,
            tokenize = 'porter unicode61 remove_diacritics 2'
        );
        """);
        if (indexIsNew)
        {
            IndexAllMessages();
        }
    }

    private void IndexAllMessages()
    {
        lock (databaseLock)
        {
            var storedMessages = new List<(string ConversationId, int Position, string MessageJson)>();
            using (var readCommand = CreateCommand(null, "SELECT conversation_id, position, message_json FROM conversation_messages;"))
            using (var reader = readCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    storedMessages.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
                }
            }

            using var transaction = connection.BeginTransaction();
            foreach (var (conversationId, position, messageJson) in storedMessages)
            {
                if (JsonNode.Parse(messageJson) is JsonObject message)
                {
                    IndexMessage(transaction, conversationId, position, message);
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Adds one message to the search index if it has something said in it: Shapto's own words (not the header and injected memories),
    /// or Closure's answer. Tool results and empty tool-call steps are left out.
    /// </summary>
    private void IndexMessage(SqliteTransaction transaction, string conversationId, int position, JsonObject message)
    {
        var role = GetString(message["role"]);
        var text = role switch
        {
            "user" => GetString(message["rhodes_display_text"]) ?? GetString(message["content"]),
            "assistant" => GetString(message["content"]),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Execute(transaction, "INSERT INTO chat_search (conversation_id, position, role, sent_at, text) VALUES ($id, $position, $role, $sentAt, $text);",
            ("$id", conversationId),
            ("$position", position),
            ("$role", role),
            ("$sentAt", GetString(message["rhodes_sent_at"])),
            ("$text", text));
    }

    /// <summary>
    /// Keyword search over every chat except the one given (usually the chat she's in, which she can already see).
    /// Messages that were summarized away still count, so she can find what a summary left out.
    /// </summary>
    public IReadOnlyList<ChatSearchHit> SearchMessages(string query, string? excludedConversationId, int limit)
    {
        var searchTerms = SearchWordPattern().Matches(query.ToLowerInvariant())
            .Select(match => match.Value)
            .Distinct()
            .Take(MaximumSearchTerms)
            .ToList();
        if (searchTerms.Count == 0)
        {
            return [];
        }

        var matchExpression = string.Join(" OR ", searchTerms.Select(term => "\"" + term + "\""));
        lock (databaseLock)
        {
            using var command = CreateCommand(null, """
                SELECT chat_search.conversation_id, conversations.title, chat_search.position, chat_search.role, chat_search.sent_at,
                       snippet(chat_search, 4, '«', '»', '…', 24)
                FROM chat_search
                JOIN conversations ON conversations.id = chat_search.conversation_id
                WHERE chat_search MATCH $match AND chat_search.conversation_id <> $excluded
                ORDER BY bm25(chat_search)
                LIMIT $limit;
                """,
                ("$match", matchExpression),
                ("$excluded", excludedConversationId ?? ""),
                ("$limit", limit));
            using var reader = command.ExecuteReader();
            var hits = new List<ChatSearchHit>();
            while (reader.Read())
            {
                hits.Add(new ChatSearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5)));
            }

            return hits;
        }
    }

    private void AddColumnIfMissing(string columnName, string columnDefinition)
    {
        using var command = CreateCommand(null, "SELECT COUNT(*) FROM pragma_table_info('conversations') WHERE name = $name;", ("$name", columnName));
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            Execute(null, $"ALTER TABLE conversations ADD COLUMN {columnName} {columnDefinition};");
        }
    }

    public Conversation Create()
    {
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var conversation = new Conversation(Guid.NewGuid().ToString("N"))
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMilliseconds).ToLocalTime(),
        };
        lock (databaseLock)
        {
            Execute(null, "INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, '', $now, $now);",
                ("$id", conversation.Id),
                ("$now", nowMilliseconds));
        }

        return conversation;
    }

    public Conversation? Load(string conversationId)
    {
        lock (databaseLock)
        {
            using var conversationCommand = CreateCommand(null, "SELECT title, last_user_message_at, summary, summary_covers_through, tool_results_collapsed_before, created_at, title_is_manual FROM conversations WHERE id = $id;", ("$id", conversationId));
            using var conversationReader = conversationCommand.ExecuteReader();
            if (!conversationReader.Read())
            {
                return null;
            }

            var conversation = new Conversation(conversationId)
            {
                Title = conversationReader.GetString(0),
                LastUserMessageAt = conversationReader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(conversationReader.GetInt64(1)),
                Summary = conversationReader.GetString(2),
                SummaryCoversThrough = conversationReader.GetInt32(3),
                ToolResultsCollapsedBefore = conversationReader.GetInt32(4),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(conversationReader.GetInt64(5)).ToLocalTime(),
                TitleIsManual = conversationReader.GetInt64(6) != 0,
            };

            using var messagesCommand = CreateCommand(null, "SELECT message_json FROM conversation_messages WHERE conversation_id = $id ORDER BY position;", ("$id", conversationId));
            using var messagesReader = messagesCommand.ExecuteReader();
            while (messagesReader.Read())
            {
                conversation.Messages.Add(JsonNode.Parse(messagesReader.GetString(0))!.AsObject());
            }

            return conversation;
        }
    }

    public void SaveCompaction(Conversation conversation)
    {
        lock (databaseLock)
        {
            Execute(null, "UPDATE conversations SET summary = $summary, summary_covers_through = $summaryCoversThrough, tool_results_collapsed_before = $toolResultsCollapsedBefore WHERE id = $id;",
                ("$summary", conversation.Summary),
                ("$summaryCoversThrough", conversation.SummaryCoversThrough),
                ("$toolResultsCollapsedBefore", conversation.ToolResultsCollapsedBefore),
                ("$id", conversation.Id));
        }
    }

    /// <summary>
    /// Deletes stored messages from fromPosition onwards, after the last turn was removed to run it again.
    /// </summary>
    public void DeleteMessagesFrom(Conversation conversation, int fromPosition)
    {
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "DELETE FROM conversation_messages WHERE conversation_id = $id AND position >= $fromPosition;",
                ("$id", conversation.Id),
                ("$fromPosition", fromPosition));
            Execute(transaction, "DELETE FROM chat_search WHERE conversation_id = $id AND position >= $fromPosition;",
                ("$id", conversation.Id),
                ("$fromPosition", fromPosition));
            Execute(transaction, "UPDATE conversations SET updated_at = $now, last_user_message_at = $lastUserMessageAt, tool_results_collapsed_before = $toolResultsCollapsedBefore WHERE id = $id;",
                ("$now", nowMilliseconds),
                ("$lastUserMessageAt", conversation.LastUserMessageAt?.ToUnixTimeMilliseconds()),
                ("$toolResultsCollapsedBefore", conversation.ToolResultsCollapsedBefore),
                ("$id", conversation.Id));
            transaction.Commit();
        }
    }

    /// <summary>
    /// Deletes a chat and all its messages. Returns false if there was no such chat.
    /// </summary>
    public bool Delete(string conversationId)
    {
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "DELETE FROM conversation_messages WHERE conversation_id = $id;", ("$id", conversationId));
            Execute(transaction, "DELETE FROM chat_search WHERE conversation_id = $id;", ("$id", conversationId));
            var deletedRows = Execute(transaction, "DELETE FROM conversations WHERE id = $id;", ("$id", conversationId));
            transaction.Commit();
            return deletedRows > 0;
        }
    }

    public void SaveTitle(Conversation conversation)
    {
        lock (databaseLock)
        {
            Execute(null, "UPDATE conversations SET title = $title, title_is_manual = $titleIsManual WHERE id = $id;",
                ("$title", conversation.Title),
                ("$titleIsManual", conversation.TitleIsManual ? 1 : 0),
                ("$id", conversation.Id));
        }
    }

    public void SaveNewMessages(Conversation conversation, int firstNewIndex)
    {
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "DELETE FROM chat_search WHERE conversation_id = $id AND position >= $firstNewIndex;",
                ("$id", conversation.Id),
                ("$firstNewIndex", firstNewIndex));
            for (var position = firstNewIndex; position < conversation.Messages.Count; position++)
            {
                Execute(transaction, "INSERT OR REPLACE INTO conversation_messages (conversation_id, position, message_json) VALUES ($id, $position, $json);",
                    ("$id", conversation.Id),
                    ("$position", position),
                    ("$json", conversation.Messages[position].ToJsonString()));
                IndexMessage(transaction, conversation.Id, position, conversation.Messages[position]);
            }

            Execute(transaction, "UPDATE conversations SET title = $title, updated_at = $now, last_user_message_at = $lastUserMessageAt WHERE id = $id;",
                ("$title", conversation.Title),
                ("$now", nowMilliseconds),
                ("$lastUserMessageAt", conversation.LastUserMessageAt?.ToUnixTimeMilliseconds()),
                ("$id", conversation.Id));
            transaction.Commit();
        }
    }

    public IReadOnlyList<ConversationSummary> List(int limit)
    {
        lock (databaseLock)
        {
            using var command = CreateCommand(null, """
                SELECT conversations.id, conversations.title, conversations.created_at, conversations.updated_at, COUNT(conversation_messages.position), conversations.summary_covers_through
                FROM conversations
                LEFT JOIN conversation_messages ON conversation_messages.conversation_id = conversations.id
                GROUP BY conversations.id
                ORDER BY conversations.updated_at DESC
                LIMIT $limit;
                """,
                ("$limit", limit));

            using var reader = command.ExecuteReader();
            var summaries = new List<ConversationSummary>();
            while (reader.Read())
            {
                summaries.Add(new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetInt32(4),
                    reader.GetInt32(5) > 0));
            }

            return summaries;
        }
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]{2,}")]
    private static partial Regex SearchWordPattern();

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
}