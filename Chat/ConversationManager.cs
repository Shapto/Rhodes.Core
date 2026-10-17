using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

public sealed record DisplayMessage(string Role, string Text, string? Reasoning, string? ToolName);

/// <summary>
/// Hands out one shared Conversation object per chat id, loading it from rhodes.db the first time.
/// </summary>
public sealed class ConversationManager
{
    private readonly ConversationStore conversationStore;
    private readonly Dictionary<string, Conversation> loadedConversations = [];
    private readonly object loadLock = new();

    public ConversationManager(ConversationStore conversationStore)
    {
        this.conversationStore = conversationStore;
    }

    public Conversation Create()
    {
        var conversation = conversationStore.Create();
        lock (loadLock)
        {
            loadedConversations[conversation.Id] = conversation;
        }

        return conversation;
    }

    public Conversation? Get(string conversationId)
    {
        lock (loadLock)
        {
            if (loadedConversations.TryGetValue(conversationId, out var loadedConversation))
            {
                return loadedConversation;
            }

            var conversation = conversationStore.Load(conversationId);
            if (conversation is not null)
            {
                loadedConversations[conversationId] = conversation;
            }

            return conversation;
        }
    }

    /// <summary>
    /// What a page shows: what Shapto typed (not the header or injected memories), her answers with their thinking, tool results.
    /// </summary>
    public static IReadOnlyList<DisplayMessage> ToDisplayMessages(Conversation conversation)
    {
        return conversation.Messages
            .Select(message => new DisplayMessage(
                GetString(message["role"]) ?? "",
                GetString(message["rhodes_display_text"]) ?? GetString(message["content"]) ?? "",
                GetString(message["rhodes_reasoning"]),
                GetString(message["rhodes_tool_name"])))
            .Where(displayMessage => displayMessage.Text.Length > 0 || displayMessage.Reasoning is not null)
            .ToList();
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}