using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rhodes.Core.Chat;
using Rhodes.Core.Tools;

namespace Rhodes.Core.Approvals;

/// <summary>
/// What a tool wants to do, in words for the approval card. Category is one of ApprovalCategories; AlwaysKey is what
/// "always allow" remembers (a host, a command's start, or a workspace folder) and AlwaysLabel how the button shows it.
/// </summary>
public sealed record ApprovalRequest(string Category, string Title, string Detail, string AlwaysKey, string AlwaysLabel);

public sealed record ApprovalRule(long Id, string Category, string Key, DateTimeOffset CreatedAt);

/// <summary>
/// The page's answer to an approval card: "once", "always" or "deny".
/// </summary>
public sealed record ApprovalAnswer(string Decision);

public enum ApprovalDecision
{
    AllowOnce,
    AlwaysAllow,
    Deny,
    Unavailable,
}

public static class ApprovalCategories
{
    public const string Web = "web";
    public const string Write = "write";
    public const string Command = "command";
}

/// <summary>
/// A tool that has to be approved before it runs. Returns null when nothing needs approving (or the arguments are
/// wrong, so the tool itself can explain the mistake).
/// </summary>
public interface IApprovalRequired
{
    ApprovalRequest? DescribeApproval(JsonElement arguments);
}

/// <summary>
/// Asks Shapto before a tool writes a file, runs a command or goes on the web. The question goes to the open chat page
/// as an approval card; the tool call waits until they answer (or 10 minutes pass, which counts as no).
/// "Always allow" rules are kept in rhodes.db and can be removed on the dashboard.
/// </summary>
public sealed class ApprovalService : IDisposable
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(10);

    private readonly SqliteConnection connection;
    private readonly object databaseLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> pendingRequests = new();
    private readonly ILogger<ApprovalService> logger;

    public ApprovalService(string databasePath, ILogger<ApprovalService> logger)
    {
        this.logger = logger;
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
        CREATE TABLE IF NOT EXISTS approval_rules (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            category    TEXT NOT NULL,
            rule_key    TEXT NOT NULL,
            created_at  INTEGER NOT NULL,
            UNIQUE (category, rule_key)
        );
        """);
    }

    /// <summary>
    /// Asks, unless an "always allow" rule already covers it. With no page to ask on (a heartbeat tick), the answer is Unavailable.
    /// </summary>
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, Func<ChatEvent, Task>? emitEvent, CancellationToken cancellationToken)
    {
        if (IsAlwaysAllowed(request))
        {
            return ApprovalDecision.AllowOnce;
        }

        if (emitEvent is null)
        {
            return ApprovalDecision.Unavailable;
        }

        var requestId = Guid.NewGuid().ToString("N")[..12];
        var answer = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingRequests[requestId] = answer;
        try
        {
            await emitEvent(new ChatEvent("approval", JsonSerializer.Serialize(new
            {
                id = requestId,
                category = request.Category,
                title = request.Title,
                detail = request.Detail,
                alwaysLabel = request.AlwaysLabel,
            })));
            await emitEvent(new ChatEvent("status", "waiting for your approval…"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AnswerTimeout);
            await using var registration = timeout.Token.Register(() => answer.TrySetResult(ApprovalDecision.Deny));
            var decision = await answer.Task;
            cancellationToken.ThrowIfCancellationRequested();

            if (decision == ApprovalDecision.AlwaysAllow)
            {
                AddRule(request.Category, request.AlwaysKey);
            }

            logger.LogInformation("Approval {Category} \"{Title}\": {Decision}", request.Category, request.Title, decision);
            return decision;
        }
        finally
        {
            pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// The gate every tool call goes through: null means go ahead, otherwise the text she gets back instead of a result.
    /// </summary>
    public async Task<string?> CheckToolAsync(ICoreTool tool, JsonElement arguments, Func<ChatEvent, Task>? emitEvent, CancellationToken cancellationToken, string? askedBy = null)
    {
        if (tool is not IApprovalRequired approvalRequired || approvalRequired.DescribeApproval(arguments) is not { } describedRequest)
        {
            return null;
        }

        var request = askedBy is null ? describedRequest : describedRequest with { Title = askedBy + ": " + describedRequest.Title };

        var decision = await RequestAsync(request, emitEvent, cancellationToken);
        return decision switch
        {
            ApprovalDecision.AllowOnce or ApprovalDecision.AlwaysAllow => null,
            ApprovalDecision.Deny => "Error: Shapto said no to this. Don't try it again this turn; carry on without it, or ask them what they'd prefer.",
            _ => "Error: this needs Shapto's approval and there's no chat open to ask in. Leave it for when they're back.",
        };
    }

    /// <summary>
    /// Shapto's answer from the page. Returns false if that card is no longer waiting (answered, timed out, or the turn stopped).
    /// </summary>
    public bool Resolve(string requestId, ApprovalDecision decision)
    {
        return pendingRequests.TryGetValue(requestId, out var answer) && answer.TrySetResult(decision);
    }

    /// <summary>
    /// A web rule covers its host and subdomains, a command rule every command starting with it, a write rule its folder and everything inside.
    /// </summary>
    public bool IsAlwaysAllowed(ApprovalRequest request)
    {
        var requestKey = request.AlwaysKey;
        return ListRules().Any(rule => rule.Category == request.Category && request.Category switch
        {
            ApprovalCategories.Web => requestKey.Equals(rule.Key, StringComparison.OrdinalIgnoreCase) || requestKey.EndsWith("." + rule.Key, StringComparison.OrdinalIgnoreCase),
            ApprovalCategories.Command => NormalizeCommand(requestKey).StartsWith(NormalizeCommand(rule.Key), StringComparison.OrdinalIgnoreCase),
            ApprovalCategories.Write => (requestKey + "/").StartsWith(rule.Key.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase),
            _ => false,
        });
    }

    public IReadOnlyList<ApprovalRule> ListRules()
    {
        lock (databaseLock)
        {
            using var command = CreateCommand("SELECT id, category, rule_key, created_at FROM approval_rules ORDER BY category, rule_key;");
            using var reader = command.ExecuteReader();
            var rules = new List<ApprovalRule>();
            while (reader.Read())
            {
                rules.Add(new ApprovalRule(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime()));
            }

            return rules;
        }
    }

    public bool DeleteRule(long ruleId)
    {
        lock (databaseLock)
        {
            return Execute("DELETE FROM approval_rules WHERE id = $id;", ("$id", ruleId)) > 0;
        }
    }

    private void AddRule(string category, string key)
    {
        lock (databaseLock)
        {
            Execute("INSERT OR IGNORE INTO approval_rules (category, rule_key, created_at) VALUES ($category, $key, $now);",
                ("$category", category),
                ("$key", key),
                ("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds()));
        }
    }

    private static string NormalizeCommand(string commandText)
    {
        return string.Join(' ', commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
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