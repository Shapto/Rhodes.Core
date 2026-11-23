using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

/// <summary>
/// Names chats: once after the first reply (deferred until Shapto goes quiet, see ChatTitlingService),
/// and again after each summary, so the title follows where the chat went.
/// Never overwrites a title Shapto set by hand.
/// </summary>
public sealed class ChatTitler
{
    private const int MaximumTitleCharacters = 60;
    private const int MaximumExcerptCharacters = 1500;
    private const string TitleInstructions = "You name chats between Shapto and Closure, their AI. Reply with only the title: 2 to 5 words about the topic, no quotes, no emojis, no punctuation at the end.";

    private readonly LmStudioClient lmStudioClient;
    private readonly ConversationStore conversationStore;
    private readonly ILogger<ChatTitler> logger;

    public ChatTitler(LmStudioClient lmStudioClient, ConversationStore conversationStore, ILogger<ChatTitler> logger)
    {
        this.lmStudioClient = lmStudioClient;
        this.conversationStore = conversationStore;
        this.logger = logger;
    }

    /// <summary>
    /// True when the chat is waiting for a model title and its first turn has a reply to name it by.
    /// </summary>
    public static bool NeedsTitle(Conversation conversation)
    {
        return conversation.TitlePending && !conversation.TitleIsManual && FindFirstReply(conversation) is not null;
    }

    /// <summary>
    /// Names a chat from its first message and first reply. Called by ChatTitlingService while Shapto is quiet;
    /// it never waits for the chat's lock, so a turn that is running always wins.
    /// Doesn't warm the cache: the caller does that once the lock is released.
    /// Returns true when a title request went to the model (so the cache needs warming), false if the chat was busy or needs no title.
    /// If the model's answer is unusable, the chat keeps its first-message title and isn't retried.
    /// </summary>
    public async Task<bool> TitlePendingChatAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        if (!await conversation.TurnLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (conversation.TitleIsManual && conversation.TitlePending)
            {
                conversation.TitlePending = false;
                conversationStore.SaveTitle(conversation);
                return false;
            }

            if (!NeedsTitle(conversation))
            {
                return false;
            }

            var (firstMessage, firstReply) = FindFirstReply(conversation)!.Value;
            var title = await GenerateTitleAsync($"Shapto: {Shorten(firstMessage)}\nClosure: {Shorten(firstReply)}", cancellationToken);
            if (title is null)
            {
                logger.LogWarning("Chat {ConversationId}: the model's title was unusable; keeping \"{Title}\"", conversation.Id, conversation.Title);
                conversation.TitlePending = false;
                conversationStore.SaveTitle(conversation);
                return true;
            }

            ApplyTitle(conversation, title);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Usually the model server being down; the chat stays pending and is tried again at the next quiet spell.
            logger.LogWarning(exception, "Titling chat {ConversationId} failed", conversation.Id);
            return false;
        }
        finally
        {
            conversation.TurnLock.Release();
        }
    }

    /// <summary>
    /// Shapto's first message and the final reply of that first turn (the last assistant message before his second message).
    /// Null when the first turn has no reply with text yet.
    /// </summary>
    private static (string FirstMessage, string FirstReply)? FindFirstReply(Conversation conversation)
    {
        var firstUserIndex = conversation.Messages.FindIndex(message => GetString(message["role"]) == "user");
        if (firstUserIndex < 0)
        {
            return null;
        }

        var secondUserIndex = conversation.Messages.FindIndex(firstUserIndex + 1, message => GetString(message["role"]) == "user");
        var firstTurnEnd = secondUserIndex < 0 ? conversation.Messages.Count : secondUserIndex;
        var firstReply = conversation.Messages
            .Skip(firstUserIndex + 1)
            .Take(firstTurnEnd - firstUserIndex - 1)
            .Where(message => GetString(message["role"]) == "assistant")
            .Select(message => GetString(message["content"]) ?? "")
            .LastOrDefault(content => content.Length > 0);
        if (firstReply is null)
        {
            return null;
        }

        var firstMessage = GetString(conversation.Messages[firstUserIndex]["rhodes_display_text"]) ?? GetString(conversation.Messages[firstUserIndex]["content"]) ?? "";
        return (firstMessage, firstReply);
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
        conversation.TitlePending = false;
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
