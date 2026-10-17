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

    public bool TitleIsManual { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public List<JsonObject> Messages { get; } = [];

    public DateTimeOffset? LastUserMessageAt { get; set; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    public string Summary { get; set; } = "";

    public int SummaryCoversThrough { get; set; }

    public int ToolResultsCollapsedBefore { get; set; }

    public int? LastContextTokens { get; set; }

    public int LastRequestCharacters { get; set; }
}

public sealed record ChatRequest(string Message, string? Client, string? Thinking, string? ConversationId, IReadOnlyList<string>? AttachmentIds);

public sealed record TitleRequest(string Title);

public sealed record ToolStep(string ToolName, string Arguments, string Result);

public sealed record TurnResult(string Reply, string Reasoning, IReadOnlyList<ToolStep> ToolSteps, IReadOnlyList<string> InjectedMemories, double ElapsedSeconds, ThinkingLevel ThinkingLevel);

public sealed record ChatEvent(string Type, string? Text = null, string? ToolName = null, string? Result = null);
