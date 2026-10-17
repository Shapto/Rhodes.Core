using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Approvals;
using Rhodes.Core.Chat;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Little Handy, Closure's helper. She hands him one self-contained job (dig something up, give an overview, review code);
    /// he works it in his own fresh context with read-only tools and hands back a short report, so the digging never fills her chat.
    /// One job at a time. His steps show in her trail, and anything that needs approval asks Shapto as usual, marked as his.
    /// </summary>
    public sealed class LittleHandyTool : ICoreTool
    {
        private const int MaximumSteps = 20;
        private const int MaximumReportCharacters = 10000;
        private const int MaximumShownResultCharacters = 400;
        private const double ContextLimit = 0.75;
        private const string AskedBy = "Little Handy";

        private static readonly HashSet<string> HandyToolNames =
        [
            "search_memory", "search_chats", "read_chat", "search_document", "read_document", "web_search", "web_fetch", "list_files", "read_file",
        ];

        private static readonly Dictionary<string, string> ModeInstructions = new()
        {
            ["dig"] = "Find the specific thing asked for. Quote the exact lines, messages or passages that answer it, with where they are.",
            ["overview"] = "Map it: the structure, the main parts and how they connect, where things start, anything that stands out. Read enough to be accurate, not every file.",
            ["review"] = "Review the code named in the task: bugs, risky spots, missing error handling, confusing parts. Point at exact files and lines. Keep real problems separate from style.",
        };

        private const string SystemPrompt =
            "You are Little Handy, Closure's helper. Closure is an AI who works with Shapto; she hands you one job at a time and you report back to her. " +
            "You work alone with read-only tools: you can't change anything, and you can't see her chat, only the task she gave you.\n" +
            "Use your tools thoroughly before you conclude, and never claim to have read something you didn't. " +
            "When you're done, write the report: what you found, with file paths and line numbers, message numbers or links; then anything you couldn't check. " +
            "No greetings or filler. Under 600 words unless the job really needs more.";

        private readonly IServiceProvider serviceProvider;
        private readonly LmStudioClient lmStudioClient;
        private readonly ApprovalService approvalService;
        private readonly ILogger<LittleHandyTool> logger;
        private readonly int contextTokens;
        private readonly SemaphoreSlim jobLock = new(1, 1);

        public LittleHandyTool(IServiceProvider serviceProvider, LmStudioClient lmStudioClient, ApprovalService approvalService, IConfiguration configuration, ILogger<LittleHandyTool> logger)
        {
            this.serviceProvider = serviceProvider;
            this.lmStudioClient = lmStudioClient;
            this.approvalService = approvalService;
            this.logger = logger;
            contextTokens = configuration.GetValue("Chat:ContextTokens", 64000);
        }

        public string Name => "little_handy";

        public string Description =>
            "Hand a big, self-contained job to Little Handy, your helper: dig something up (in a repo, a long log, past chats), get an overview of a codebase, " +
            "or review code in your workspace. He uses read-only tools in his own context and returns a short report, so your context stays small. " +
            "He can't see this chat: put everything he needs in the task. One job at a time.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["task"] = new JsonObject { ["type"] = "string", ["description"] = "The job, with every link, path, document id or name he needs." },
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("dig", "overview", "review"),
                    ["description"] = "dig: find something specific. overview: map a codebase or topic. review: check code for problems.",
                },
            },
            ["required"] = new JsonArray("task", "mode"),
        };

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var task = ToolArguments.GetRequiredString(arguments, "task").Trim();
            var mode = ToolArguments.GetOptionalString(arguments, "mode")?.Trim().ToLowerInvariant() ?? "dig";
            if (!ModeInstructions.TryGetValue(mode, out var modeInstruction))
            {
                throw new ToolArgumentException("\"mode\" must be dig, overview or review.");
            }

            if (!await jobLock.WaitAsync(0, cancellationToken))
            {
                return "Error: Little Handy is already on a job. Wait for his report, or do this one yourself.";
            }

            try
            {
                return await RunJobAsync(task, mode, modeInstruction, cancellationToken);
            }
            finally
            {
                jobLock.Release();
            }
        }

        private async Task<string> RunJobAsync(string task, string mode, string modeInstruction, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var emitEvent = ToolContext.EmitEvent;
            var handyTools = serviceProvider.GetServices<ICoreTool>()
                .Where(coreTool => HandyToolNames.Contains(coreTool.Name))
                .ToList();
            var toolDefinitions = BuildToolDefinitions(handyTools);
            var requestMessages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = $"Job ({mode}): {task}\n\n{modeInstruction}" },
            };
            var completedCalls = new HashSet<string>();
            var stepCount = 0;
            var previousRequestCharacters = 0;
            int? previousPromptTokens = null;
            await EmitAsync(emitEvent, new ChatEvent("status", $"little handy: starting ({mode})…"));

            for (var stepNumber = 0; stepNumber <= MaximumSteps; stepNumber++)
            {
                var requestCharacters = requestMessages.ToJsonString().Length;
                var estimatedTokens = previousRequestCharacters > 0 && previousPromptTokens is { } knownTokens ? (double)requestCharacters * knownTokens / previousRequestCharacters : requestCharacters / 3.5;
                var contextNearlyFull = estimatedTokens > contextTokens * ContextLimit;
                var offerTools = stepNumber < MaximumSteps && !contextNearlyFull;
                var stepMessages = (JsonArray)requestMessages.DeepClone();
                if (!offerTools)
                {
                    stepMessages.Add(new JsonObject { ["role"] = "user", ["content"] = "No more tool calls. Write your report now from what you found, and list what you didn't get to." });
                }

                var responseMessage = await lmStudioClient.StreamCompletionAsync(stepMessages, offerTools ? (JsonArray)toolDefinitions.DeepClone() : null, ThinkingLevel.Light, (_, _) => Task.FromResult(true), cancellationToken);
                previousRequestCharacters = requestCharacters;
                previousPromptTokens = GetInteger(responseMessage["rhodes_prompt_tokens"]) ?? previousPromptTokens;
                var (_, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), GetString(responseMessage["reasoning_content"]));
                var toolCalls = responseMessage["tool_calls"] as JsonArray;
                if (!offerTools || toolCalls is not { Count: > 0 })
                {
                    logger.LogInformation("Little Handy finished a {Mode} job: {Steps} tool calls in {Seconds:0.0} s", mode, stepCount, stopwatch.Elapsed.TotalSeconds);
                    var report = answer.Length > MaximumReportCharacters ? answer[..MaximumReportCharacters] + "\n[report cut short]" : answer;
                    return $"Little Handy's report ({stepCount} tool calls, {stopwatch.Elapsed.TotalSeconds:0} s):\n{(report.Length == 0 ? "(he came back without a report)" : report)}";
                }

                requestMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer, ["tool_calls"] = toolCalls.DeepClone() });
                foreach (var toolCall in toolCalls)
                {
                    var toolName = GetString(toolCall!["function"]?["name"]) ?? "";
                    var argumentsNode = toolCall["function"]?["arguments"];
                    var argumentsText = argumentsNode is JsonValue argumentsValue && argumentsValue.TryGetValue<string>(out var argumentsString) ? argumentsString : argumentsNode?.ToJsonString() ?? "{}";
                    await EmitAsync(emitEvent, new ChatEvent("status", $"little handy: {toolName}…"));
                    var result = completedCalls.Add(toolName + "\n" + argumentsText) ? await RunToolAsync(handyTools, toolName, argumentsText, emitEvent, cancellationToken) : "You already made this exact call; use the result above.";
                    stepCount++;
                    var shownResult = result.Length > MaximumShownResultCharacters ? result[..MaximumShownResultCharacters] + "…" : result;
                    await EmitAsync(emitEvent, new ChatEvent("tool", argumentsText, "handy · " + toolName, shownResult));
                    requestMessages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = GetString(toolCall["id"]), ["content"] = result });
                }
            }

            return "Error: Little Handy didn't finish.";
        }

        private async Task<string> RunToolAsync(IReadOnlyList<ICoreTool> handyTools, string toolName, string argumentsText, Func<ChatEvent, Task>? emitEvent, CancellationToken cancellationToken)
        {
            var tool = handyTools.FirstOrDefault(candidateTool => candidateTool.Name == toolName);
            if (tool is null)
            {
                return $"Error: \"{toolName}\" isn't one of your tools. You have: {string.Join(", ", handyTools.Select(candidateTool => candidateTool.Name))}.";
            }

            try
            {
                using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsText) ? "{}" : argumentsText);
                var refusal = await approvalService.CheckToolAsync(tool, argumentsDocument.RootElement, emitEvent, cancellationToken, AskedBy);
                return refusal ?? await tool.ExecuteAsync(argumentsDocument.RootElement, cancellationToken);
            }
            catch (JsonException)
            {
                return "Error: the arguments weren't valid JSON.";
            }
            catch (ToolArgumentException exception)
            {
                return $"Error: {exception.Message}";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Little Handy's {ToolName} failed", toolName);
                return $"Error: the tool failed ({exception.Message}).";
            }
        }

        private static Task EmitAsync(Func<ChatEvent, Task>? emitEvent, ChatEvent chatEvent)
        {
            return emitEvent is null ? Task.CompletedTask : emitEvent(chatEvent);
        }

        private static JsonArray BuildToolDefinitions(IEnumerable<ICoreTool> coreTools)
        {
            var definitions = new JsonArray();
            foreach (var coreTool in coreTools)
            {
                definitions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = coreTool.Name, ["description"] = coreTool.Description, ["parameters"] = coreTool.ParametersSchema },
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
}