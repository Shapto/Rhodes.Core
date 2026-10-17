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

    public List<JsonObject> Messages { get; } = [];

    public DateTimeOffset? LastUserMessageAt { get; set; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);
}

public sealed record ChatRequest(string Message, string? Client, string? Thinking, string? ConversationId);

public sealed record ToolStep(string ToolName, string Arguments, string Result);

public sealed record TurnResult(string Reply, string Reasoning, IReadOnlyList<ToolStep> ToolSteps, IReadOnlyList<string> InjectedMemories, double ElapsedSeconds, ThinkingLevel ThinkingLevel);

public sealed record ChatEvent(string Type, string? Text = null, string? ToolName = null, string? Result = null);
