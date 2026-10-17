using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

/// <summary>
/// Names chats: once after the first reply, and again after each summary, so the title follows where the chat went.
/// Never overwrites a title Shapto set by hand.
/// </summary>
public sealed class ChatTitler
{
    private const int MaximumTitleCharacters = 60;
    private const int MaximumExcerptCharacters = 1500;
    private const string TitleInstructions = "You name chats between Shapto and Closure, their AI. Reply with only the title: 2 to 5 words about the topic, no quotes, no emojis, no punctuation at the end.";

    private readonly LmStudioClient lmStudioClient;
    private readonly ChatLoop chatLoop;
    private readonly ConversationStore conversationStore;
    private readonly ILogger<ChatTitler> logger;

    public ChatTitler(LmStudioClient lmStudioClient, ChatLoop chatLoop, ConversationStore conversationStore, ILogger<ChatTitler> logger)
    {
        this.lmStudioClient = lmStudioClient;
        this.chatLoop = chatLoop;
        this.conversationStore = conversationStore;
        this.logger = logger;
    }

    /// <summary>
    /// Titles a chat that has just had its first reply, then warms the cache again, because the title request replaced it.
    /// Returns the new title, or null if the chat isn't new, has a manual title, or titling failed.
    /// </summary>
    public async Task<string?> TitleNewChatAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        await conversation.TurnLock.WaitAsync(cancellationToken);
        try
        {
            var userMessages = conversation.Messages
                .Where(message => GetString(message["role"]) == "user")
                .ToList();
            if (conversation.TitleIsManual || userMessages.Count != 1)
            {
                return null;
            }

            var firstMessage = GetString(userMessages[0]["rhodes_display_text"]) ?? "";
            var lastAssistantMessage = conversation.Messages.LastOrDefault(message => GetString(message["role"]) == "assistant");
            var firstReply = GetString(lastAssistantMessage?["content"]) ?? "";
            var title = await GenerateTitleAsync($"Shapto: {Shorten(firstMessage)}\nClosure: {Shorten(firstReply)}", cancellationToken);
            if (title is null)
            {
                return null;
            }

            ApplyTitle(conversation, title);
            await chatLoop.WarmUpCacheAsync(conversation, cancellationToken);
            return title;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Titling chat {ConversationId} failed", conversation.Id);
            return null;
        }
        finally
        {
            conversation.TurnLock.Release();
        }
    }

    /// <summary>
    /// Re-titles from the running summary after compaction. The caller must already hold conversation.TurnLock,
    /// and warms the cache itself afterwards. Returns true if the title changed.
    /// </summary>
    public async Task<bool> RetitleFromSummaryAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        if (conversation.TitleIsManual || conversation.Summary.Length == 0)
        {
            return false;
        }

        try
        {
            var lastUserMessage = conversation.Messages.LastOrDefault(message => GetString(message["role"]) == "user");
            var latestMessage = GetString(lastUserMessage?["rhodes_display_text"]) ?? "";
            var excerpt = $"Summary of the chat so far:\n{conversation.Summary}\n\nShapto's latest message: {Shorten(latestMessage)}\n\nName the chat by what it is mainly about now.";
            var title = await GenerateTitleAsync(excerpt, cancellationToken);
            if (title is null || title == conversation.Title)
            {
                return false;
            }

            ApplyTitle(conversation, title);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Re-titling chat {ConversationId} failed", conversation.Id);
            return false;
        }
    }

    private async Task<string?> GenerateTitleAsync(string excerpt, CancellationToken cancellationToken)
    {
        var titleRequest = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = TitleInstructions },
            new JsonObject { ["role"] = "user", ["content"] = "Name this chat:\n\n" + excerpt },
        };

        var responseMessage = await lmStudioClient.StreamCompletionAsync(titleRequest, null, ThinkingLevel.Off, (_, _) => Task.FromResult(true), cancellationToken);
        var (_, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), null);
        return CleanTitle(answer);
    }

    private void ApplyTitle(Conversation conversation, string title)
    {
        logger.LogInformation("Chat {ConversationId}: titled \"{Title}\"", conversation.Id, title);
        conversation.Title = title;
        conversationStore.SaveTitle(conversation);
    }

    /// <summary>
    /// Keeps the first line and strips what models like to wrap titles in: quotes, markdown, "Title:", a final period.
    /// </summary>
    private static string? CleanTitle(string rawTitle)
    {
        var firstLine = rawTitle
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        if (firstLine.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            firstLine = firstLine["Title:".Length..];
        }

        var title = firstLine.Trim().Trim('"', '\'', '*', '#', '`', '.', '!', ' ');
        if (title.Length > MaximumTitleCharacters)
        {
            title = title[..MaximumTitleCharacters].TrimEnd() + "…";
        }

        return title.Length < 2 ? null : title;
    }

    private static string Shorten(string text)
    {
        return text.Length > MaximumExcerptCharacters ? text[..MaximumExcerptCharacters] + "…" : text;
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
