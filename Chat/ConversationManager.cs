using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

public sealed record DisplayMessage(int Index, string Role, string Text, string? Reasoning, string? ToolName);
public sealed record ConversationView(string Id, string Title, string Summary, int SummaryCoversThrough, IReadOnlyList<DisplayMessage> Messages);

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
    /// What a page shows: the messages with their positions, plus where the summary begins, so the page can draw the marker.
    /// </summary>
    public static ConversationView ToView(Conversation conversation)
    {
        var displayMessages = conversation.Messages
            .Select((message, index) => new DisplayMessage(
                index,
                GetString(message["role"]) ?? "",
                GetString(message["rhodes_display_text"]) ?? GetString(message["content"]) ?? "",
                GetString(message["rhodes_reasoning"]),
                GetString(message["rhodes_tool_name"])))
            .Where(displayMessage => displayMessage.Text.Length > 0 || displayMessage.Reasoning is not null)
            .ToList();
        return new ConversationView(conversation.Id, conversation.Title, conversation.Summary, conversation.SummaryCoversThrough, displayMessages);
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}