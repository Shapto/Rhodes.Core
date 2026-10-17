using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rhodes.Core.Chat;

namespace Rhodes.Core.Heartbeat;

/// <summary>
/// One time the heartbeat woke her: when, why, what came of it, her closing note and the tools she used.
/// Outcome is one of HeartbeatOutcomes.
/// </summary>
public sealed record HeartbeatTick(long Id, DateTimeOffset StartedAt, double Seconds, string Trigger, string Outcome, string Note, IReadOnlyList<ToolStep> Steps, int? PromptTokens);

public sealed record JournalEntry(long Id, long? TickId, DateTimeOffset WrittenAt, string Text);

public static class HeartbeatOutcomes
{
    public const string Running = "running";
    public const string Nothing = "nothing";
    public const string Looked = "looked";
    public const string Journal = "journal";
    public const string Interrupted = "interrupted";
    public const string Failed = "failed";
}

/// <summary>
/// The heartbeat's log and Closure's journal, both in rhodes.db.
/// </summary>
public sealed class HeartbeatStore : IDisposable
{
    private const int MaximumStoredResultCharacters = 2000;

    private readonly SqliteConnection connection;
    private readonly object databaseLock = new();

    public HeartbeatStore(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA busy_timeout = 5000;");
        Execute("""
        CREATE TABLE IF NOT EXISTS heartbeat_ticks (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at     INTEGER NOT NULL,
            seconds        REAL NOT NULL DEFAULT 0,
            trigger        TEXT NOT NULL,
            outcome        TEXT NOT NULL,
            note           TEXT NOT NULL DEFAULT '',
            steps          TEXT NOT NULL DEFAULT '[]',
            prompt_tokens  INTEGER
        );
        CREATE TABLE IF NOT EXISTS journal_entries (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            tick_id     INTEGER,
            written_at  INTEGER NOT NULL,
            text        TEXT NOT NULL
        );
        """);

        // A tick that was still running when the core stopped (or the laptop slept and the core was closed) never finished.
        Execute("UPDATE heartbeat_ticks SET outcome = $interrupted, note = 'The core stopped during this tick.' WHERE outcome = $running;",
            ("$interrupted", HeartbeatOutcomes.Interrupted),
            ("$running", HeartbeatOutcomes.Running));
    }

    /// <summary>
    /// Records the start of a tick and returns its id, so a journal entry written during it can point back to it.
    /// </summary>
    public long StartTick(string trigger, DateTimeOffset startedAt)
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("INSERT INTO heartbeat_ticks (started_at, trigger, outcome) VALUES ($startedAt, $trigger, $outcome); SELECT last_insert_rowid();",
                ("$startedAt", startedAt.ToUnixTimeMilliseconds()),
                ("$trigger", trigger),
                ("$outcome", HeartbeatOutcomes.Running));
            return (long)command.ExecuteScalar()!;
        }
    }

    public void FinishTick(long tickId, double seconds, string outcome, string note, IReadOnlyList<ToolStep> steps, int? promptTokens)
    {
        // Results are shortened for the log; the dashboard shows what she looked at, not every byte of it.
        var storedSteps = steps
            .Select(step => step with { Result = step.Result.Length > MaximumStoredResultCharacters ? step.Result[..MaximumStoredResultCharacters] + "\n[…shortened in the log]" : step.Result })
            .ToList();
        lock (databaseLock)
        {
            Execute("UPDATE heartbeat_ticks SET seconds = $seconds, outcome = $outcome, note = $note, steps = $steps, prompt_tokens = $promptTokens WHERE id = $id;",
                ("$seconds", Math.Round(seconds, 1)),
                ("$outcome", outcome),
                ("$note", note),
                ("$steps", JsonSerializer.Serialize(storedSteps, JsonSerializerOptions.Web)),
                ("$promptTokens", promptTokens),
                ("$id", tickId));
        }
    }

    public IReadOnlyList<HeartbeatTick> ListTicks(int limit)
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("SELECT id, started_at, seconds, trigger, outcome, note, steps, prompt_tokens FROM heartbeat_ticks ORDER BY started_at DESC LIMIT $limit;",
                ("$limit", limit));
            using var reader = command.ExecuteReader();
            var ticks = new List<HeartbeatTick>();
            while (reader.Read())
            {
                ticks.Add(new HeartbeatTick(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                    reader.GetDouble(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    JsonSerializer.Deserialize<List<ToolStep>>(reader.GetString(6), JsonSerializerOptions.Web) ?? [],
                    reader.IsDBNull(7) ? null : reader.GetInt32(7)));
            }

            return ticks;
        }
    }

    /// <summary>
    /// When the last tick that actually ran started. Interrupted ticks don't count, so one cut short by a chat doesn't delay the next.
    /// </summary>
    public DateTimeOffset? GetLastTickAt()
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("SELECT MAX(started_at) FROM heartbeat_ticks WHERE outcome <> $interrupted;",
                ("$interrupted", HeartbeatOutcomes.Interrupted));
            var result = command.ExecuteScalar();
            return result is long milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime() : null;
        }
    }

    public long AddJournalEntry(long? tickId, string text)
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("INSERT INTO journal_entries (tick_id, written_at, text) VALUES ($tickId, $writtenAt, $text); SELECT last_insert_rowid();",
                ("$tickId", tickId),
                ("$writtenAt", DateTimeOffset.Now.ToUnixTimeMilliseconds()),
                ("$text", text));
            return (long)command.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<JournalEntry> ListJournal(int limit, int offset = 0)
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("SELECT id, tick_id, written_at, text FROM journal_entries ORDER BY written_at DESC LIMIT $limit OFFSET $offset;",
                ("$limit", limit),
                ("$offset", offset));
            using var reader = command.ExecuteReader();
            var entries = new List<JournalEntry>();
            while (reader.Read())
            {
                entries.Add(new JournalEntry(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToLocalTime(),
                    reader.GetString(3)));
            }

            return entries;
        }
    }

    public int CountJournal()
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("SELECT COUNT(*) FROM journal_entries;");
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public bool DeleteJournalEntry(long entryId)
    {
        lock (databaseLock)
        {
            return Execute("DELETE FROM journal_entries WHERE id = $id;", ("$id", entryId)) > 0;
        }
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    private SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }
}