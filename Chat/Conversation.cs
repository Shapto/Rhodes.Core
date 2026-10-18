using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

/// <summary>
/// One chat: its messages as the model sees them (plus Rhodes-only "rhodes_" fields), timing and its write lock.
/// </summary>
public sealed class Conversation
{
    public Conversation(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public string Title { get; set; } = "";

    /// <summary>
    /// True once Shapto renames the chat by hand; automatic titling never overwrites it.
    /// </summary>
    public bool TitleIsManual { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public List<JsonObject> Messages { get; } = [];

    public DateTimeOffset? LastUserMessageAt { get; set; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    public string Summary { get; set; } = "";

    public int SummaryCoversThrough { get; set; }

    public int ToolResultsCollapsedBefore { get; set; }

    /// <summary>
    /// Messages before this index have been through the background memory pass (or the one before compaction).
    /// </summary>
    public int MemoryPassThrough { get; set; }

    public int? LastContextTokens { get; set; }

    public int LastRequestCharacters { get; set; }

    /// <summary>
    /// Of LastRequestCharacters: how much was the system prompt plus summary, and how much was injected memories.
    /// </summary>
    public int LastSystemCharacters { get; set; }

    public int LastMemoryCharacters { get; set; }
}

public sealed record ChatRequest(string Message, string? Client, string? Thinking, string? ConversationId, IReadOnlyList<string>? AttachmentIds, bool? Regenerate);

/// <summary>
/// What Shapto's last message said and which files it had, after the turn was removed to run it again.
/// </summary>
public sealed record RemovedTurn(string MessageText, IReadOnlyList<string> AttachmentIds);

/// <summary>
/// Numbers for one finished turn: the last model call's prompt and cache, everything generated across all calls.
/// </summary>
public sealed record TurnStatistics(int? PromptTokens, int? CachedTokens, int GeneratedTokens, double? TokensPerSecond, double Seconds, int ModelCalls);

/// <summary>
/// Rough split of the context in tokens: persona plus summary, injected memories, and everything else.
/// </summary>
public sealed record ContextSplit(int System, int Memory, int Chat);

public enum ChatDeleteOutcome
{
    Deleted,
    NotFound,
    Busy,
}

public sealed record TitleRequest(string Title);

public sealed record PinRequest(bool Pinned);

public sealed record ToolStep(string ToolName, string Arguments, string Result);

public sealed record TurnResult(string Reply, string Reasoning, IReadOnlyList<ToolStep> ToolSteps, IReadOnlyList<string> InjectedMemories, double ElapsedSeconds, ThinkingLevel ThinkingLevel, TurnStatistics Statistics);

public sealed record ChatEvent(string Type, string? Text = null, string? ToolName = null, string? Result = null);