using Rhodes.Core.Memory;
using Rhodes.Core.Tools;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Rhodes.Core.Chat.LmStudioClient;

namespace Rhodes.Core.Chat;

/// <summary>
/// Runs one turn: stamps the message, injects memories, then calls the model and its tools
/// until she answers in plain text or the step limit is reached.
/// </summary>
public sealed class ChatLoop
{
    private const int MaximumToolSteps = 10;
    private const int AutoInjectCount = 3;
    private const double AutoInjectMinimumSimilarity = 0.5;
    private const int AutoInjectMaximumCharacters = 1500;

    private readonly LmStudioClient lmStudioClient;
    private readonly MemoryRanker memoryRanker;
    private readonly IReadOnlyList<ICoreTool> tools;
    private readonly JsonArray toolDefinitions;
    private readonly string personaPath;
    private readonly ILogger<ChatLoop> logger;
    private readonly int maximumThinkingCharacters;
    private readonly ConversationStore conversationStore;

    public ChatLoop(LmStudioClient lmStudioClient, MemoryRanker memoryRanker, ConversationStore conversationStore, IEnumerable<ICoreTool> tools, string personaPath, int maximumThinkingCharacters, ILogger<ChatLoop> logger)
    {
        this.lmStudioClient = lmStudioClient;
        this.memoryRanker = memoryRanker;
        this.tools = tools.ToList();
        this.personaPath = personaPath;
        this.logger = logger;
        toolDefinitions = BuildToolDefinitions(this.tools);
        this.maximumThinkingCharacters = maximumThinkingCharacters;
        this.conversationStore = conversationStore;
    }

    public async Task<TurnResult> RunTurnAsync(Conversation conversation, string userText, string clientName, string thinkingSetting, Func<ChatEvent, Task>? onEvent, CancellationToken cancellationToken)
    {
        await conversation.TurnLock.WaitAsync(cancellationToken);
        var messageCountBeforeTurn = conversation.Messages.Count;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var now = DateTimeOffset.Now;

            var (messageText, forcedLevel) = ParseThinkingCommand(userText);
            var thinkingLevel = forcedLevel ?? thinkingSetting switch
            {
                "on" => ThinkingLevel.Full,
                "off" => ThinkingLevel.Off,
                _ => ThinkingClassifier.Classify(messageText),
            };
            await EmitAsync(onEvent, new ChatEvent("thinkingLevel", thinkingLevel.ToString().ToLowerInvariant()));

            var injectedMemories = FindMemoriesToInject(messageText);
            if (injectedMemories.Count > 0)
            {
                await EmitAsync(onEvent, new ChatEvent("memories", string.Join("\n", injectedMemories)));
            }

            var userContent = BuildUserContent(messageText, clientName, now, conversation.LastUserMessageAt, injectedMemories);
            conversation.Messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent,
                ["rhodes_display_text"] = messageText,
            });

            var systemPrompt = await File.ReadAllTextAsync(personaPath, cancellationToken);
            var toolSteps = new List<ToolStep>();
            var reasoningParts = new List<string>();

            for (var stepNumber = 0; stepNumber <= MaximumToolSteps; stepNumber++)
            {
                var offerTools = stepNumber < MaximumToolSteps;
                var requestMessages = BuildRequestMessages(systemPrompt, conversation);
                var thinkingFilter = new StreamingThinkingFilter();
                var thinkingCharacters = 0;
                var separateReasoningSeen = false;
                var thinkingLimitHit = false;

                var responseMessage = await lmStudioClient.StreamCompletionAsync(
                    requestMessages,
                    offerTools ? (JsonArray)toolDefinitions.DeepClone() : null,
                    thinkingLevel,
                    async (deltaKind, deltaText) =>
                    {
                        if (deltaKind == LmStudioClient.ReasoningDelta)
                        {
                            separateReasoningSeen = true;
                            thinkingCharacters += deltaText.Length;
                            await EmitAsync(onEvent, new ChatEvent("reasoning", deltaText));
                        }
                        else
                        {
                            if (!separateReasoningSeen && !thinkingFilter.ThinkingEnded)
                            {
                                thinkingCharacters += deltaText.Length;
                            }

                            foreach (var chatEvent in thinkingFilter.Feed(deltaText))
                            {
                                await EmitAsync(onEvent, chatEvent);
                            }

                            if (thinkingFilter.StopRequested)
                            {
                                return false;
                            }
                        }

                        if (thinkingCharacters > maximumThinkingCharacters)
                        {
                            thinkingLimitHit = true;
                            return false;
                        }

                        return true;
                    },
                    cancellationToken);

                if (thinkingLimitHit)
                {
                    throw new ThinkingLimitException($"Stopped: she was still thinking after about {thinkingCharacters / 4} tokens. Ask again, or more specifically.");
                }

                var promptTokens = GetInteger(responseMessage["rhodes_prompt_tokens"]);
                if (promptTokens is not null)
                {
                    conversation.LastContextTokens = promptTokens + (GetInteger(responseMessage["rhodes_completion_tokens"]) ?? 0);
                }

                foreach (var chatEvent in thinkingFilter.Flush())
                {
                    await EmitAsync(onEvent, chatEvent);
                }

                var (reasoning, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), GetString(responseMessage["reasoning_content"]));
                if (reasoning.Length > 0)
                {
                    reasoningParts.Add(reasoning);
                }

                var toolCalls = responseMessage["tool_calls"] as JsonArray;
                if (offerTools && toolCalls is not null && toolCalls.Count > 0)
                {
                    conversation.Messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = answer,
                        ["tool_calls"] = toolCalls.DeepClone(),
                        ["rhodes_reasoning"] = reasoning,
                    });

                    foreach (var toolCall in toolCalls)
                    {
                        await EmitAsync(onEvent, new ChatEvent("status", $"running {GetString(toolCall!["function"]?["name"])}…"));
                        var toolStep = await RunToolCallAsync(toolCall!, cancellationToken);
                        toolSteps.Add(toolStep);
                        await EmitAsync(onEvent, new ChatEvent("tool", toolStep.Arguments, toolStep.ToolName, toolStep.Result));
                        conversation.Messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = GetString(toolCall!["id"]),
                            ["content"] = toolStep.Result,
                            ["rhodes_tool_name"] = toolStep.ToolName,
                        });
                    }

                    continue;
                }

                conversation.Messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = answer,
                    ["rhodes_reasoning"] = reasoning,
                });
                conversation.LastUserMessageAt = now;

                if (conversation.Title.Length == 0)
                {
                    conversation.Title = messageText.Length > 60 ? messageText[..60] + "…" : messageText;
                }

                conversationStore.SaveNewMessages(conversation, messageCountBeforeTurn);
                return new TurnResult(answer, string.Join("\n\n", reasoningParts), toolSteps, injectedMemories, stopwatch.Elapsed.TotalSeconds, thinkingLevel);
            }

            throw new InvalidOperationException("The tool loop ended without an answer.");
        }
        catch
        {
            // A failed turn leaves no half-finished messages behind.
            conversation.Messages.RemoveRange(messageCountBeforeTurn, conversation.Messages.Count - messageCountBeforeTurn);
            throw;
        }
        finally
        {
            conversation.TurnLock.Release();
        }
    }

    /// <summary>
    /// "/quick ..." turns thinking off and "/think ..." forces full thinking; the command itself is removed.
    /// </summary>
    private static (string MessageText, ThinkingLevel? ForcedLevel) ParseThinkingCommand(string userText)
    {
        var trimmedText = userText.TrimStart();
        if (trimmedText.StartsWith("/quick", StringComparison.OrdinalIgnoreCase))
        {
            return (trimmedText["/quick".Length..].Trim(), ThinkingLevel.Off);
        }

        if (trimmedText.StartsWith("/think", StringComparison.OrdinalIgnoreCase))
        {
            return (trimmedText["/think".Length..].Trim(), ThinkingLevel.Full);
        }

        return (userText, null);
    }

    private static Task EmitAsync(Func<ChatEvent, Task>? onEvent, ChatEvent chatEvent)
    {
        return onEvent is null ? Task.CompletedTask : onEvent(chatEvent);
    }

    private async Task<ToolStep> RunToolCallAsync(JsonNode toolCall, CancellationToken cancellationToken)
    {
        var toolName = GetString(toolCall["function"]?["name"]) ?? "";
        var argumentsNode = toolCall["function"]?["arguments"];
        var argumentsText = argumentsNode is JsonValue argumentsValue && argumentsValue.TryGetValue<string>(out var argumentsString) ? argumentsString : argumentsNode?.ToJsonString() ?? "{}";

        var tool = tools.FirstOrDefault(candidateTool => candidateTool.Name == toolName);
        if (tool is null)
        {
            var availableNames = string.Join(", ", tools.Select(candidateTool => candidateTool.Name));
            return new ToolStep(toolName, argumentsText, $"Error: there is no tool named \"{toolName}\". Available tools: {availableNames}.");
        }

        try
        {
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsText) ? "{}" : argumentsText);
            var result = await tool.ExecuteAsync(argumentsDocument.RootElement, cancellationToken);
            return new ToolStep(toolName, argumentsText, result);
        }
        catch (JsonException)
        {
            return new ToolStep(toolName, argumentsText, "Error: the arguments weren't valid JSON. Call again with a JSON object.");
        }
        catch (ToolArgumentException exception)
        {
            return new ToolStep(toolName, argumentsText, $"Error: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Tool {ToolName} failed", toolName);
            return new ToolStep(toolName, argumentsText, $"Error: the tool failed ({exception.Message}). Tell Shapto it failed instead of guessing the result.");
        }
    }

    /// <summary>
    /// The top hit may get in on one shared word; every other memory needs at least two,
    /// so a single incidental word in the message doesn't drag in unrelated facts.
    /// </summary>
    public IReadOnlyList<string> FindMemoriesToInject(string userText)
    {
        var messageWords = MemoryStore.GetSearchWords(userText);
        var requiredSharedWords = messageWords.Count <= 2 ? 1 : 2;
        return memoryRanker.Rank(userText, AutoInjectCount, includeArchive: false, countAsAccess: false)
            .Where(rankedMemory => !rankedMemory.Memory.Content.Contains("think>", StringComparison.OrdinalIgnoreCase))
            .Where(rankedMemory => rankedMemory.Similarity >= AutoInjectMinimumSimilarity && CountSharedWords(messageWords, rankedMemory.Memory) >= requiredSharedWords)
            .Select(rankedMemory => $"{rankedMemory.Memory.Type}: {rankedMemory.Memory.Content}")
            .ToList();
    }

    private static int CountSharedWords(IReadOnlyList<string> messageWords, MemoryRecord memory)
    {
        var memoryWords = MemoryStore.GetSearchWords(memory.Content + " " + string.Join(" ", memory.Tags))
            .Select(TrimCommonSuffix)
            .ToHashSet();
        return messageWords.Count(messageWord => memoryWords.Contains(TrimCommonSuffix(messageWord)));
    }

    /// <summary>
    /// Rough stand-in for the search index's stemming, so "mods" and "mod" count as the same word.
    /// </summary>
    private static string TrimCommonSuffix(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            return word[..^3];
        }

        if (word.Length > 4 && (word.EndsWith("ed", StringComparison.Ordinal) || word.EndsWith("es", StringComparison.Ordinal)))
        {
            return word[..^2];
        }

        return word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;
    }

    /// <summary>
    /// Header first, then injected memories, then the message. All of it goes in the user message,
    /// never the system prompt, so the system prompt stays identical and LM Studio can reuse its cache.
    /// </summary>
    private static string BuildUserContent(string userText, string clientName, DateTimeOffset now, DateTimeOffset? lastMessageAt, IReadOnlyList<string> injectedMemories)
    {
        var builder = new StringBuilder();
        builder.Append('[')
            .Append(now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" · ").Append(clientName)
            .Append(" · ").Append(DescribeGap(now, lastMessageAt))
            .AppendLine("]");

        if (injectedMemories.Count > 0)
        {
            builder.AppendLine("[Memories that may be relevant]");
            var usedCharacters = 0;
            foreach (var memoryLine in injectedMemories)
            {
                if (usedCharacters + memoryLine.Length > AutoInjectMaximumCharacters)
                {
                    break;
                }

                builder.Append("- ").AppendLine(memoryLine);
                usedCharacters += memoryLine.Length;
            }

            builder.AppendLine();
        }

        builder.Append(userText);
        return builder.ToString();
    }

    private static string DescribeGap(DateTimeOffset now, DateTimeOffset? lastMessageAt)
    {
        if (lastMessageAt is null)
        {
            return "first message in this chat";
        }

        var gap = now - lastMessageAt.Value;
        if (gap.TotalMinutes < 1)
        {
            return "just now";
        }

        if (gap.TotalHours < 1)
        {
            return $"{(int)gap.TotalMinutes} min since last message";
        }

        return gap.TotalDays < 1 ? $"{(int)gap.TotalHours} h since last message" : $"{(int)gap.TotalDays} days since last message";
    }

    /// <summary>
    /// What the model sees: the system prompt (plus the running summary, if any), then the messages after the summary,
    /// with old tool results collapsed and Rhodes-only fields removed. Records the size for compaction estimates.
    /// </summary>
    internal static JsonArray BuildRequestMessages(string systemPrompt, Conversation conversation)
    {
        var systemContent = conversation.Summary.Length == 0 ? systemPrompt : systemPrompt + "\n\n## Earlier in this conversation (summary)\n" + conversation.Summary;
        var requestMessages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemContent } };
        var totalCharacters = systemContent.Length;

        for (var index = conversation.SummaryCoversThrough; index < conversation.Messages.Count; index++)
        {
            var requestMessage = conversation.Messages[index].DeepClone().AsObject();
            var toolName = GetString(requestMessage["rhodes_tool_name"]);
            var privateKeys = requestMessage
                .Select(property => property.Key)
                .Where(key => key.StartsWith("rhodes_", StringComparison.Ordinal))
                .ToList();
            foreach (var privateKey in privateKeys)
            {
                requestMessage.Remove(privateKey);
            }

            if (index < conversation.ToolResultsCollapsedBefore && GetString(requestMessage["role"]) == "tool")
            {
                requestMessage["content"] = $"[Earlier {toolName ?? "tool"} result, collapsed to save space. Call the tool again if you need it.]";
            }

            totalCharacters += requestMessage.ToJsonString().Length;
            requestMessages.Add(requestMessage);
        }

        conversation.LastRequestCharacters = totalCharacters;
        return requestMessages;
    }

    private static JsonArray BuildToolDefinitions(IEnumerable<ICoreTool> coreTools)
    {
        var definitions = new JsonArray();
        foreach (var coreTool in coreTools)
        {
            definitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = coreTool.Name,
                    ["description"] = coreTool.Description,
                    ["parameters"] = coreTool.ParametersSchema,
                },
            });
        }

        return definitions;
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static int? GetInteger(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    }
}
