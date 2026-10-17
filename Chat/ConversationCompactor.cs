using System.Text;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

/// <summary>
/// Shrinks what the model sees once a chat passes 70% of the context: first collapses old tool results,
/// then, if still above 40% and enough would be folded in, folds everything before the last turns into a running summary.
/// Afterwards it warms llama-server's cache with the new prompt. The full chat in rhodes.db is never changed.
/// </summary>
public sealed class ConversationCompactor
{
    private const double StartFraction = 0.7;
    private const double TargetFraction = 0.4;
    private const double MinimumFoldFraction = 0.1;
    private const int RecentTurnsKept = 6;
    private const int MaximumTranscriptCharacters = 120_000;
    private const int MaximumToolResultCharactersInTranscript = 600;
    private const int CollapsedToolResultCharacters = 120;
    private const int MessageOverheadCharacters = 20;

    private readonly LmStudioClient lmStudioClient;
    private readonly ChatLoop chatLoop;
    private readonly ConversationStore conversationStore;
    private readonly string personaPath;
    private readonly int contextTokens;
    private readonly ILogger<ConversationCompactor> logger;
    private readonly ChatTitler chatTitler;

    public ConversationCompactor(LmStudioClient lmStudioClient, ChatLoop chatLoop, ChatTitler chatTitler, ConversationStore conversationStore, string personaPath, int contextTokens, ILogger<ConversationCompactor> logger)
    {
        this.lmStudioClient = lmStudioClient;
        this.chatLoop = chatLoop;
        this.conversationStore = conversationStore;
        this.personaPath = personaPath;
        this.contextTokens = contextTokens;
        this.logger = logger;
        this.chatTitler = chatTitler;
    }

    /// <summary>
    /// Returns true if anything was compacted. onProgress gets a short status line for each stage:
    /// "summarizing N earlier messages", then "warming up…".
    /// </summary>
    public async Task<bool> CompactIfNeededAsync(Conversation conversation, Func<string, Task>? onProgress, CancellationToken cancellationToken)
    {
        if (conversation.LastContextTokens is not { } usedTokens || usedTokens < contextTokens * StartFraction)
        {
            return false;
        }

        await conversation.TurnLock.WaitAsync(cancellationToken);
        try
        {
            var keepFromIndex = FindKeepFromIndex(conversation);
            if (keepFromIndex <= conversation.SummaryCoversThrough)
            {
                logger.LogWarning("Chat {ConversationId} is at {UsedTokens} tokens, but its recent turns alone are that large; nothing older to compact", conversation.Id, usedTokens);
                return false;
            }

            var tokensPerCharacter = conversation.LastRequestCharacters > 0 ? (double)usedTokens / conversation.LastRequestCharacters : 0.3;
            var systemPrompt = await File.ReadAllTextAsync(personaPath, cancellationToken);

            // Stage 1: collapse old tool results. Cheap, no model call.
            ChatLoop.BuildRequestMessages(systemPrompt, conversation);
            var charactersBeforeCollapse = conversation.LastRequestCharacters;
            conversation.ToolResultsCollapsedBefore = keepFromIndex;
            ChatLoop.BuildRequestMessages(systemPrompt, conversation);
            var collapsedAnything = conversation.LastRequestCharacters < charactersBeforeCollapse;
            var estimatedTokens = (int)(conversation.LastRequestCharacters * tokensPerCharacter);
            if (collapsedAnything)
            {
                logger.LogInformation("Chat {ConversationId}: collapsed old tool results, {UsedTokens} -> about {EstimatedTokens} tokens", conversation.Id, usedTokens, estimatedTokens);
            }

            // Stage 2: summarize, but only when enough would be folded in to be worth a model call and a cache rebuild.
            var summarized = false;
            if (estimatedTokens > contextTokens * TargetFraction)
            {
                var foldedMessageCount = keepFromIndex - conversation.SummaryCoversThrough;
                var foldedTokens = (int)(MeasureCharacters(conversation, conversation.SummaryCoversThrough, keepFromIndex) * tokensPerCharacter);
                if (foldedTokens < contextTokens * MinimumFoldFraction)
                {
                    logger.LogInformation("Chat {ConversationId}: only about {FoldedTokens} tokens could be summarized; waiting until there's more", conversation.Id, foldedTokens);
                }
                else
                {
                    await ReportAsync(onProgress, $"summarizing {foldedMessageCount} earlier messages");
                    var summaryText = await WriteSummaryAsync(conversation, keepFromIndex, cancellationToken);
                    if (string.IsNullOrWhiteSpace(summaryText))
                    {
                        throw new InvalidOperationException("The model returned an empty summary.");
                    }

                    conversation.Summary = summaryText;
                    conversation.SummaryCoversThrough = keepFromIndex;
                    ChatLoop.BuildRequestMessages(systemPrompt, conversation);
                    estimatedTokens = (int)(conversation.LastRequestCharacters * tokensPerCharacter);
                    summarized = true;
                    logger.LogInformation("Chat {ConversationId}: summarized {FoldedMessageCount} messages, now about {EstimatedTokens} tokens", conversation.Id, foldedMessageCount, estimatedTokens);
                }
            }

            if (!collapsedAnything && !summarized)
            {
                return false;
            }

            conversationStore.SaveCompaction(conversation);
            conversation.LastContextTokens = estimatedTokens;

            if (summarized)
            {
                await chatTitler.RetitleFromSummaryAsync(conversation, cancellationToken);
            }

            await ReportAsync(onProgress, "warming up…");
            try
            {
                await chatLoop.WarmUpCacheAsync(conversation, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Cache warm-up for chat {ConversationId} failed; the next message will rebuild it", conversation.Id);
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Compaction of chat {ConversationId} failed", conversation.Id);
            return false;
        }
        finally
        {
            conversation.TurnLock.Release();
        }
    }

    private static Task ReportAsync(Func<string, Task>? onProgress, string progressText)
    {
        return onProgress is null ? Task.CompletedTask : onProgress(progressText);
    }

    /// <summary>
    /// The index of the user message that starts the last RecentTurnsKept turns; everything before it may be compacted.
    /// </summary>
    private static int FindKeepFromIndex(Conversation conversation)
    {
        var userMessageIndexes = conversation.Messages
            .Select((message, index) => (Message: message, Index: index))
            .Where(entry => GetString(entry.Message["role"]) == "user")
            .Select(entry => entry.Index)
            .ToList();
        return userMessageIndexes.Count > RecentTurnsKept ? userMessageIndexes[^RecentTurnsKept] : 0;
    }

    /// <summary>
    /// Rough size of a range of messages as the model currently sees them.
    /// Collapsed tool results count as their short placeholder; Rhodes-only fields like reasoning don't count.
    /// </summary>
    private static int MeasureCharacters(Conversation conversation, int fromIndex, int toIndex)
    {
        var totalCharacters = 0;
        for (var index = fromIndex; index < toIndex; index++)
        {
            var message = conversation.Messages[index];
            var isCollapsedToolResult = index < conversation.ToolResultsCollapsedBefore && GetString(message["role"]) == "tool";
            var contentCharacters = GetString(message["content"])?.Length ?? 0;
            var toolCallCharacters = message["tool_calls"]?.ToJsonString().Length ?? 0;
            totalCharacters += isCollapsedToolResult ? CollapsedToolResultCharacters : contentCharacters + toolCallCharacters + MessageOverheadCharacters;
        }

        return totalCharacters;
    }

    private async Task<string> WriteSummaryAsync(Conversation conversation, int keepFromIndex, CancellationToken cancellationToken)
    {
        var transcript = BuildTranscript(conversation, conversation.SummaryCoversThrough, keepFromIndex);
        var previousSummary = conversation.Summary.Length > 0 ? conversation.Summary : "(none yet)";
        var summaryRequest = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You write the running summary of a long conversation between Closure, an AI, and Shapto (they/them), so the conversation can continue after older messages are removed. Plain text, English, no emojis.",
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"""
                    Previous summary:
                    {previousSummary}

                    Messages to fold in:
                    {transcript}

                    Write one new summary covering both, under 500 words, in exactly this shape:
                    Goals:
                    - ...
                    Decisions made:
                    - ...
                    Open threads:
                    - ...
                    Files and tools involved:
                    - ...
                    Carry over every item from the previous summary unless the new messages settle or reverse it; you may shorten an item's wording, but never drop it. A settled open thread moves to Decisions made.
                    Record only what was actually said or decided. No interpretation, no guesses about anyone's intentions or feelings.
                    Under "Files and tools involved", list only real file names and tool names that were used, such as search_memory.
                    If a section has nothing, write "- none". Shorter is better than padded.
                    Keep names, numbers, file names and decisions exactly. Leave out small talk.
                    """,
            },
        };

        var responseMessage = await lmStudioClient.StreamCompletionAsync(summaryRequest, null, ThinkingLevel.Off, (_, _) => Task.FromResult(true), cancellationToken);
        var (_, summaryText) = ThinkingSplitter.Split(GetString(responseMessage["content"]), null);
        return summaryText;
    }

    private static string BuildTranscript(Conversation conversation, int fromIndex, int toIndex)
    {
        var builder = new StringBuilder();
        for (var index = fromIndex; index < toIndex; index++)
        {
            var message = conversation.Messages[index];
            var role = GetString(message["role"]);
            var text = role == "user" ? GetString(message["rhodes_display_text"]) ?? GetString(message["content"]) : GetString(message["content"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var speaker = role switch
            {
                "user" => "Shapto",
                "assistant" => "Closure",
                _ => $"[{GetString(message["rhodes_tool_name"]) ?? "tool"} result]",
            };
            var shownText = role == "tool" && text.Length > MaximumToolResultCharactersInTranscript ? text[..MaximumToolResultCharactersInTranscript] + "…" : text;
            builder.Append(speaker).Append(": ").AppendLine(shownText).AppendLine();
        }

        var transcript = builder.ToString();
        return transcript.Length > MaximumTranscriptCharacters ? "[earliest part cut]\n" + transcript[^MaximumTranscriptCharacters..] : transcript;
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}