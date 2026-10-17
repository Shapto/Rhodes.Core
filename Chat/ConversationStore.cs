using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Rhodes.Core.Chat;

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

/// <summary>
/// Saves conversations in rhodes.db, separate from memory.db: chat history is Rhodes' own data, memories are hers.
/// Messages are stored as the model sees them, plus Rhodes-only "rhodes_" fields that never go to the model.
/// </summary>
public sealed class ConversationStore : IDisposable
{
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
    }

    public Conversation Create()
    {
        var conversation = new Conversation(Guid.NewGuid().ToString("N"));
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            using var conversationCommand = CreateCommand(null, "SELECT title, last_user_message_at FROM conversations WHERE id = $id;", ("$id", conversationId));
            using var conversationReader = conversationCommand.ExecuteReader();
            if (!conversationReader.Read())
            {
                return null;
            }

            var conversation = new Conversation(conversationId)
            {
                Title = conversationReader.GetString(0),
                LastUserMessageAt = conversationReader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(conversationReader.GetInt64(1)),
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

    /// <summary>
    /// Saves the messages a finished turn added, from firstNewIndex onwards, plus the title and timing.
    /// </summary>
    public void SaveNewMessages(Conversation conversation, int firstNewIndex)
    {
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (databaseLock)
        {
            using var transaction = connection.BeginTransaction();
            for (var position = firstNewIndex; position < conversation.Messages.Count; position++)
            {
                Execute(transaction, "INSERT OR REPLACE INTO conversation_messages (conversation_id, position, message_json) VALUES ($id, $position, $json);",
                    ("$id", conversation.Id),
                    ("$position", position),
                    ("$json", conversation.Messages[position].ToJsonString()));
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
                SELECT conversations.id, conversations.title, conversations.created_at, conversations.updated_at, COUNT(conversation_messages.position)
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
                    reader.GetInt32(4)));
            }

            return summaries;
        }
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
}