using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Chat;
using Rhodes.Core.Tools;

namespace Rhodes.Core.Heartbeat;

/// <summary>
/// Runs one heartbeat tick: Closure wakes with a short look at recent chats and her journal,
/// may use a few read-only tools, may write one journal entry, and ends with a one-line note for the away log.
/// Doing nothing is a normal result.
/// </summary>
public sealed class HeartbeatRunner
{
    private const int MaximumToolSteps = 4;
    private const int RecentChatCount = 3;
    private const int RecentJournalCount = 3;
    private const int MaximumSummaryCharacters = 700;
    private const int MaximumMessageCharacters = 300;
    private const int MaximumAnswerCharacters = 4000;

    private static readonly string[] OfflineToolNames = ["search_memory", "search_chats", "read_chat", "search_document", "read_document"];
    private static readonly string[] WebToolNames = ["web_search", "web_fetch"];

    private readonly LmStudioClient lmStudioClient;
    private readonly HeartbeatStore heartbeatStore;
    private readonly ConversationStore conversationStore;
    private readonly ConversationManager conversationManager;
    private readonly ChatLoop chatLoop;
    private readonly IReadOnlyList<ICoreTool> readOnlyTools;
    private readonly string personaPath;
    private readonly ILogger<HeartbeatRunner> logger;

    public HeartbeatRunner(LmStudioClient lmStudioClient, HeartbeatStore heartbeatStore, ConversationStore conversationStore, ConversationManager conversationManager, ChatLoop chatLoop, IEnumerable<ICoreTool> coreTools, string personaPath, bool allowWeb, ILogger<HeartbeatRunner> logger)
    {
        this.lmStudioClient = lmStudioClient;
        this.heartbeatStore = heartbeatStore;
        this.conversationStore = conversationStore;
        this.conversationManager = conversationManager;
        this.chatLoop = chatLoop;
        this.personaPath = personaPath;
        this.logger = logger;

        // Only tools that look things up. save_memory is never offered here, and the web only when Heartbeat:AllowWeb is on.
        var allowedNames = allowWeb ? OfflineToolNames.Concat(WebToolNames).ToHashSet() : OfflineToolNames.ToHashSet();
        readOnlyTools = coreTools
            .Where(coreTool => allowedNames.Contains(coreTool.Name))
            .ToList();
        AllowWeb = allowWeb;
    }

    public bool AllowWeb { get; }

    public async Task<HeartbeatTick> RunTickAsync(string trigger, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var tickId = heartbeatStore.StartTick(trigger, startedAt);
        var journalTool = new WriteJournalTool(heartbeatStore, tickId);
        var tickTools = readOnlyTools.Append(journalTool).ToList();
        var toolDefinitions = BuildToolDefinitions(tickTools);
        var steps = new List<ToolStep>();
        int? promptTokens = null;

        try
        {
            var systemPrompt = await File.ReadAllTextAsync(personaPath, cancellationToken);
            var requestMessages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = BuildTickPrompt(startedAt) },
            };
            var completedCalls = new HashSet<string>();
            var note = "";

            for (var stepNumber = 0; stepNumber <= MaximumToolSteps; stepNumber++)
            {
                var offerTools = stepNumber < MaximumToolSteps;
                var answerCharacters = 0;
                // A copy each step: the client attaches the list to its request body, and a JSON node can only have one parent.
                var responseMessage = await lmStudioClient.StreamCompletionAsync(
                    (JsonArray)requestMessages.DeepClone(),
                    offerTools ? (JsonArray)toolDefinitions.DeepClone() : null,
                    ThinkingLevel.Off,
                    (_, deltaText) =>
                    {
                        answerCharacters += deltaText.Length;
                        return Task.FromResult(answerCharacters <= MaximumAnswerCharacters);
                    },
                    cancellationToken);
                promptTokens = GetInteger(responseMessage["rhodes_prompt_tokens"]) ?? promptTokens;

                var (_, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), GetString(responseMessage["reasoning_content"]));
                var toolCalls = responseMessage["tool_calls"] as JsonArray;
                if (offerTools && toolCalls is { Count: > 0 })
                {
                    requestMessages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = answer,
                        ["tool_calls"] = toolCalls.DeepClone(),
                    });
                    foreach (var toolCall in toolCalls)
                    {
                        var (toolName, argumentsText) = ReadToolCall(toolCall!);
                        var callKey = toolName + "\n" + argumentsText;
                        var result = completedCalls.Add(callKey) ? await RunToolAsync(tickTools, toolName, argumentsText, cancellationToken) : "You already made this exact call; use the result above.";
                        steps.Add(new ToolStep(toolName, argumentsText, result));
                        requestMessages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = GetString(toolCall!["id"]),
                            ["content"] = result,
                        });
                    }

                    continue;
                }

                note = answer;
                break;
            }

            var usedTool = steps.Any(step => step.ToolName != journalTool.Name);
            var outcome = journalTool.EntryWritten ? HeartbeatOutcomes.Journal : usedTool ? HeartbeatOutcomes.Looked : HeartbeatOutcomes.Nothing;
            var shownNote = ShortenNote(note, outcome);
            heartbeatStore.FinishTick(tickId, stopwatch.Elapsed.TotalSeconds, outcome, shownNote, steps, promptTokens);
            logger.LogInformation("Heartbeat tick {TickId} ({Trigger}): {Outcome} in {Seconds:0.0} s · {Note}", tickId, trigger, outcome, stopwatch.Elapsed.TotalSeconds, shownNote);
            return new HeartbeatTick(tickId, startedAt, stopwatch.Elapsed.TotalSeconds, trigger, outcome, shownNote, steps, promptTokens);
        }
        catch (OperationCanceledException)
        {
            heartbeatStore.FinishTick(tickId, stopwatch.Elapsed.TotalSeconds, HeartbeatOutcomes.Interrupted, "Stopped to make room for a chat.", steps, promptTokens);
            logger.LogInformation("Heartbeat tick {TickId} stopped for a chat", tickId);
            throw;
        }
        catch (Exception exception)
        {
            heartbeatStore.FinishTick(tickId, stopwatch.Elapsed.TotalSeconds, HeartbeatOutcomes.Failed, exception.Message, steps, promptTokens);
            logger.LogWarning(exception, "Heartbeat tick {TickId} failed", tickId);
            return new HeartbeatTick(tickId, startedAt, stopwatch.Elapsed.TotalSeconds, trigger, HeartbeatOutcomes.Failed, exception.Message, steps, promptTokens);
        }
    }

    /// <summary>
    /// The tick replaced the chat in llama-server's single cache slot. Re-reading the most recent chat now means
    /// Shapto's next message there is answered at normal speed instead of re-reading everything first.
    /// </summary>
    public async Task WarmUpLatestChatAsync(CancellationToken cancellationToken)
    {
        var latestSummary = conversationStore.List(1).FirstOrDefault();
        var conversation = latestSummary is null ? null : conversationManager.Get(latestSummary.Id);
        if (conversation is null || conversation.Messages.Count == 0 || !await conversation.TurnLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await chatLoop.WarmUpCacheAsync(conversation, cancellationToken);
        }
        finally
        {
            conversation.TurnLock.Release();
        }
    }

    /// <summary>
    /// Everything she wakes up to. It goes in the user message so the system prompt stays her normal persona.
    /// </summary>
    private string BuildTickPrompt(DateTimeOffset now)
    {
        var recentChats = conversationStore.List(RecentChatCount);
        var builder = new StringBuilder();
        builder.Append('[').Append(now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" · heartbeat");
        if (recentChats.Count > 0)
        {
            builder.Append(" · Shapto's last chat activity ").Append(DescribeAge(now, recentChats[0].UpdatedAt.ToLocalTime())).Append(" ago");
        }

        builder.AppendLine("]");
        builder.AppendLine("This is your own time between conversations. Shapto isn't here and nobody is waiting for a reply; they only see your journal and the one-line note you end with.");
        builder.AppendLine();

        if (recentChats.Count == 0)
        {
            builder.AppendLine("There are no chats yet.");
        }
        else
        {
            builder.AppendLine("Recent chats, newest first:");
            foreach (var chatSummary in recentChats)
            {
                AppendChat(builder, now, chatSummary);
            }
        }

        builder.AppendLine();
        var journalEntries = heartbeatStore.ListJournal(RecentJournalCount);
        if (journalEntries.Count == 0)
        {
            builder.AppendLine("Your journal is empty so far.");
        }
        else
        {
            builder.AppendLine("Your latest journal entries (don't repeat them):");
            foreach (var journalEntry in journalEntries)
            {
                builder.Append("- ").Append(journalEntry.WrittenAt.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)).Append(": ").AppendLine(Shorten(journalEntry.Text, MaximumMessageCharacters));
            }
        }

        var lookupTools = string.Join(", ", readOnlyTools.Select(readOnlyTool => readOnlyTool.Name));
        builder.AppendLine();
        builder.AppendLine("What you can do now:");
        builder.AppendLine("- Nothing. Most of the time that's the right answer.");
        builder.AppendLine($"- Look something up ({lookupTools}) if a chat left a question open that you could answer better next time.");
        builder.AppendLine("- Write one journal entry with write_journal: something you noticed or thought about, as a note to yourself. Not a to-do list or questions for Shapto. Only if it's worth reading.");
        builder.AppendLine("You can't message Shapto, save memories or change anything from here, so don't claim you did. Don't invent events.");
        builder.AppendLine("End with one short line saying what you did, like \"Nothing to do.\" or \"Wrote about the workbench idea.\"");
        return builder.ToString();
    }

    private void AppendChat(StringBuilder builder, DateTimeOffset now, ConversationSummary chatSummary)
    {
        builder.Append("- \"").Append(chatSummary.Title.Length == 0 ? "untitled" : chatSummary.Title).Append("\" · ").Append(DescribeAge(now, chatSummary.UpdatedAt.ToLocalTime())).AppendLine(" ago");
        var conversation = conversationManager.Get(chatSummary.Id);
        if (conversation is null)
        {
            return;
        }

        if (conversation.Summary.Length > 0)
        {
            builder.Append("  Summary: ").AppendLine(Shorten(conversation.Summary, MaximumSummaryCharacters));
        }

        var lastUserMessage = conversation.Messages.LastOrDefault(message => GetString(message["role"]) == "user");
        var lastAnswer = conversation.Messages.LastOrDefault(message => GetString(message["role"]) == "assistant" && !string.IsNullOrWhiteSpace(GetString(message["content"])));
        if (lastUserMessage is not null)
        {
            builder.Append("  Shapto last said: ").AppendLine(Shorten(GetString(lastUserMessage["rhodes_display_text"]) ?? "", MaximumMessageCharacters));
        }

        if (lastAnswer is not null)
        {
            builder.Append("  You last said: ").AppendLine(Shorten(GetString(lastAnswer["content"]) ?? "", MaximumMessageCharacters));
        }
    }

    private async Task<string> RunToolAsync(IReadOnlyList<ICoreTool> tickTools, string toolName, string argumentsText, CancellationToken cancellationToken)
    {
        var tool = tickTools.FirstOrDefault(candidateTool => candidateTool.Name == toolName);
        if (tool is null)
        {
            return $"Error: \"{toolName}\" isn't available during a heartbeat. Available: {string.Join(", ", tickTools.Select(candidateTool => candidateTool.Name))}.";
        }

        try
        {
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsText) ? "{}" : argumentsText);
            return await tool.ExecuteAsync(argumentsDocument.RootElement, cancellationToken);
        }
        catch (JsonException)
        {
            return "Error: the arguments weren't valid JSON. Call again with a JSON object.";
        }
        catch (ToolArgumentException exception)
        {
            return $"Error: {exception.Message}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Heartbeat tool {ToolName} failed", toolName);
            return $"Error: the tool failed ({exception.Message}).";
        }
    }

    /// <summary>
    /// Her closing line for the away log. If she wrote more than one line, the last one is kept, since that's where
    /// the prompt asks for it; if she wrote nothing, the outcome speaks for itself.
    /// </summary>
    private static string ShortenNote(string note, string outcome)
    {
        var closingLine = note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (closingLine.Length == 0)
        {
            return outcome == HeartbeatOutcomes.Journal ? "Wrote in her journal." : outcome == HeartbeatOutcomes.Looked ? "Looked a few things up." : "Nothing to do.";
        }

        return Shorten(closingLine, 200);
    }

    private static string Shorten(string text, int maximumCharacters)
    {
        var singleLine = string.Join(' ', text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length > maximumCharacters ? singleLine[..maximumCharacters].TrimEnd() + "…" : singleLine;
    }

    private static string DescribeAge(DateTimeOffset now, DateTimeOffset then)
    {
        var age = now - then;
        return age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)} min" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h" : (int)age.TotalDays == 1 ? "1 day" : $"{(int)age.TotalDays} days";
    }

    private static (string ToolName, string ArgumentsText) ReadToolCall(JsonNode toolCall)
    {
        var toolName = GetString(toolCall["function"]?["name"]) ?? "";
        var argumentsNode = toolCall["function"]?["arguments"];
        var argumentsText = argumentsNode is JsonValue argumentsValue && argumentsValue.TryGetValue<string>(out var argumentsString) ? argumentsString : argumentsNode?.ToJsonString() ?? "{}";
        return (toolName, argumentsText);
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