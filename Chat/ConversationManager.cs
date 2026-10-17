using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

public sealed record DisplayMessage(int Index, string Role, string Text, string? Reasoning, string? ToolName, JsonNode? Attachments, string? SentAt, JsonNode? Statistics);
public sealed record ConversationView(string Id, string Title, string Summary, int SummaryCoversThrough, int? ContextTokens, int ContextLimit, ContextSplit? ContextSplit, IReadOnlyList<DisplayMessage> Messages);

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
    /// Deletes a chat, unless a turn or compaction is running in it right now.
    /// </summary>
    public ChatDeleteOutcome Delete(string conversationId)
    {
        lock (loadLock)
        {
            if (loadedConversations.TryGetValue(conversationId, out var loadedConversation))
            {
                if (!loadedConversation.TurnLock.Wait(0))
                {
                    return ChatDeleteOutcome.Busy;
                }

                loadedConversations.Remove(conversationId);
                loadedConversation.TurnLock.Release();
            }

            return conversationStore.Delete(conversationId) ? ChatDeleteOutcome.Deleted : ChatDeleteOutcome.NotFound;
        }
    }

    /// <summary>
    /// Scales the last request's character counts to its real token count. Null until the chat has had a reply since the core started.
    /// </summary>
    public static ContextSplit? EstimateContextSplit(Conversation conversation)
    {
        if (conversation.LastContextTokens is not { } usedTokens || conversation.LastRequestCharacters <= 0)
        {
            return null;
        }

        var tokensPerCharacter = (double)usedTokens / conversation.LastRequestCharacters;
        var systemTokens = (int)(conversation.LastSystemCharacters * tokensPerCharacter);
        var memoryTokens = (int)(conversation.LastMemoryCharacters * tokensPerCharacter);
        return new ContextSplit(systemTokens, memoryTokens, Math.Max(0, usedTokens - systemTokens - memoryTokens));
    }

    /// <summary>
    /// What a page shows: the messages with their positions, where the summary begins (so the page can draw the marker),
    /// and how full the context is.
    /// </summary>
    public static ConversationView ToView(Conversation conversation, int contextLimit)
    {
        var displayMessages = conversation.Messages
            .Select((message, index) => new DisplayMessage(
                index,
                GetString(message["role"]) ?? "",
                GetString(message["rhodes_display_text"]) ?? GetString(message["content"]) ?? "",
                GetString(message["rhodes_reasoning"]),
                GetString(message["rhodes_tool_name"]),
                message["rhodes_attachments"]?.DeepClone(),
                GetString(message["rhodes_sent_at"]),
                message["rhodes_statistics"]?.DeepClone()))
            .Where(displayMessage => displayMessage.Text.Length > 0 || displayMessage.Reasoning is not null || displayMessage.Attachments is JsonArray { Count: > 0 })
            .ToList();
        return new ConversationView(conversation.Id, conversation.Title, conversation.Summary, conversation.SummaryCoversThrough, conversation.LastContextTokens, contextLimit, EstimateContextSplit(conversation), displayMessages);
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}   