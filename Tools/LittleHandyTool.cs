using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhodes.Core.Approvals;
using Rhodes.Core.Chat;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Little Handy, Closure's helper. She hands him one self-contained job (dig something up, give an overview, review code);
    /// he works it in his own fresh context with read-only tools and hands back a short report, so the digging never fills her chat.
    /// One job at a time. His steps show in her trail, and anything that needs approval asks Shapto as usual, marked as his.
    /// </summary>
    public sealed partial class LittleHandyTool : ICoreTool
    {
        private const int MaximumSteps = 20;
        private const int MaximumReportCharacters = 10000;
        private const int MaximumShownResultCharacters = 400;
        private const double ContextLimit = 0.75;
        private const string AskedBy = "Little Handy";

        private static readonly HashSet<string> HandyToolNames =
        [
            "search_memory", "search_chats", "read_chat", "search_document", "read_document", "web_search", "web_fetch", "list_files", "read_file",
            "search_knowledge", "read_knowledge",
        ];

        /// <summary>
        /// The line a claim check ends with when every claim checked out, so Rhodes knows the draft can go out as it is.
        /// </summary>
        public const string AllClaimsVerifiedMarker = "ALL_CLAIMS_VERIFIED";

        private const int MaximumCheckedDraftCharacters = 12000;
        private const int MaximumLibraryLookups = 15;

        // Names too common to be worth looking up: they exist everywhere and would only crowd out the interesting ones.
        private static readonly HashSet<string> CommonCodeNames = new(StringComparer.Ordinal)
        {
            "ToString", "Equals", "GetHashCode", "GetType", "Add", "Remove", "Contains", "Count", "Length", "WriteLine", "Write", "Log", "Message", "Warning", "Error",
            "Serializable", "Obsolete", "print", "require", "pairs", "ipairs", "tostring", "tonumber",
        };

        private static readonly Dictionary<string, string> ModeInstructions = new()
        {
            ["dig"] = "Find the specific thing asked for. Quote the exact lines, messages or passages that answer it, with where they are.",
            ["overview"] = "Map it: the structure, the main parts and how they connect, where things start, anything that stands out. Read enough to be accurate, not every file.",
            ["review"] = "Review the code named in the task: bugs, risky spots, missing error handling, confusing parts. Check that every API, method and setting it uses really exists (search_knowledge, read_file). Point at exact files and lines. Keep real problems separate from style.",
            ["verify"] =
                "Check the facts in Closure's draft answer before Shapto sees it. List every factual claim in it: about Shapto's code, files, setup or plans, and about APIs, " +
                "class and method names, file paths, settings, versions and how things behave. Skip opinions, advice, jokes and style. Mark each claim:\n" +
                "- VERIFIED: shown in what she looked at this turn, or confirmed now with your tools. Say where.\n" +
                "- WRONG: your tools show otherwise. Say what is true and where you saw it.\n" +
                "- UNVERIFIED: you couldn't confirm it either way.\n" +
                "Check every claim that isn't already covered: search_knowledge first (RimWorld source and Defs, Shapto's repos, .NET docs, lore), then the other tools. " +
                "Never mark a claim VERIFIED from your own knowledge alone.\n" +
                "Code in the draft counts as claims too: check every type, method, attribute and parameter it uses, and that the call matches a real overload " +
                "(a method that exists but doesn't take those arguments is WRONG). An attribute or member that the documentation you read doesn't mention is WRONG " +
                "when the documentation shows a different way to do the same thing, otherwise UNVERIFIED. Quoted error messages and exact output are claims as well.\n" +
                "Only if every claim is VERIFIED, end your report with the line " + AllClaimsVerifiedMarker + " on its own. Otherwise don't write that word anywhere.",
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
            "Hand a big, self-contained job to Little Handy, your helper. Use him instead of doing it yourself whenever a job would take you more than about 3 reads: " +
            "a whole repo or folder, a long log or document, many past chats, or a code review. He works in his own context with read-only tools and returns a short report, " +
            "so your context stays small. When Shapto asks for Little Handy, always use him. He can't see this chat: put every link, path, id and detail he needs in the task. One job at a time.";

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

        /// <summary>
        /// Little Handy's assumption check, run by Rhodes (not by Closure) before a substantial answer goes out:
        /// he lists the draft's factual claims and checks the ones not already backed by what she looked at this turn.
        /// Returns his report when something is wrong or unverified, or null when everything checked out or he couldn't run.
        /// His context is thrown away afterwards, so the check costs her context nothing.
        /// </summary>
        public async Task<string?> CheckClaimsAsync(string shaptoMessage, string draftAnswer, string turnEvidence, CancellationToken cancellationToken)
        {
            if (!await jobLock.WaitAsync(0, cancellationToken))
            {
                return null;
            }

            try
            {
                var shownDraft = draftAnswer.Length > MaximumCheckedDraftCharacters ? draftAnswer[..MaximumCheckedDraftCharacters] + "\n[draft cut short]" : draftAnswer;
                var task = $"""
                    Shapto asked:
                    {shaptoMessage}

                    Closure's draft answer:
                    ---
                    {shownDraft}
                    ---

                    {turnEvidence}

                    {BuildLibraryLookups(draftAnswer)}
                    """;
                var report = await RunJobAsync(task, "verify", ModeInstructions["verify"], cancellationToken);
                if (ZeroToolCallsPattern().IsMatch(report) && IsCleanPass(report))
                {
                    // Passing a draft without looking anything up is just agreeing with her; send him back once to actually check.
                    logger.LogInformation("Little Handy passed the draft without checking anything; asking him to check");
                    report = await RunJobAsync(task, "verify", ModeInstructions["verify"] + "\nYour last attempt marked everything VERIFIED without a single tool call. That isn't a check. Look up every API, attribute and member in her code with search_knowledge before you mark anything.", cancellationToken);
                }

                logger.LogInformation("Little Handy's fact-check report:\n{Report}", report.Length > 4000 ? report[..4000] + "\n[report cut short in the log]" : report);
                if (report.StartsWith("Error", StringComparison.Ordinal))
                {
                    logger.LogWarning("Little Handy's fact check failed, so the draft goes out unchecked");
                    return null;
                }

                return IsCleanPass(report) ? null : report;
            }
            finally
            {
                jobLock.Release();
            }
        }

        /// <summary>
        /// Looks up every attribute, method and Harmony special name used in the draft's code blocks in the knowledge library,
        /// so Little Handy starts from facts instead of from her draft. A name the library doesn't contain at all, when the library
        /// has that topic's docs (Harmony, .NET, Godot, CC: Tweaked), is a strong sign she made it up.
        /// </summary>
        private string BuildLibraryLookups(string draftAnswer)
        {
            var knowledgeDatabase = serviceProvider.GetService<Knowledge.KnowledgeDatabase>();
            if (knowledgeDatabase is null)
            {
                return "";
            }

            // Names from code blocks, plus `inline code` in the prose, where made-up names hide just as often.
            var codeText = string.Join("\n", CodeFencePattern().Matches(draftAnswer).Select(match => match.Groups["code"].Value));
            var proseText = CodeFencePattern().Replace(draftAnswer, "");
            var codeNames = CodeNamePattern().Matches(codeText).Select(match => match.Groups["name"].Value);
            var inlineNames = InlineCodePattern().Matches(proseText)
                .Select(match => InlineNamePattern().Match(match.Groups["code"].Value))
                .Where(match => match.Success)
                .Select(match => match.Groups["name"].Value);
            var lookedUpNames = inlineNames
                .Concat(codeNames)
                .Where(name => name.Length > 2 && !CommonCodeNames.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumLibraryLookups)
                .ToList();
            if (lookedUpNames.Count == 0)
            {
                return "";
            }

            var builder = new StringBuilder("Library lookups Rhodes ran for the names in her code (\"not found\" means the exact name appears nowhere in the knowledge library):\n");
            foreach (var lookedUpName in lookedUpNames)
            {
                var searchResults = knowledgeDatabase.Search(lookedUpName, null, 10);
                var exactResult = searchResults.FirstOrDefault(result => result.Title.Contains(lookedUpName, StringComparison.Ordinal) || result.Snippet.Replace("[", "").Replace("]", "").Contains(lookedUpName, StringComparison.Ordinal));
                builder.AppendLine(exactResult is null
                    ? $"- {lookedUpName}: not found"
                    : $"- {lookedUpName}: found in {exactResult.RelativePath} lines {exactResult.StartLine}-{exactResult.EndLine} ({exactResult.Title})");
            }

            builder.Append("Treat a \"not found\" API or attribute as probably made up when the library covers that topic: mark it WRONG unless your own search finds it, and say what the documentation uses instead.");
            return builder.ToString();
        }

        /// <summary>
        /// A report passes only when its last line is the marker and it marks nothing WRONG or UNVERIFIED.
        /// Checking just for the marker let reports through that mentioned it in passing ("so I can't write ALL_CLAIMS_VERIFIED").
        /// </summary>
        private static bool IsCleanPass(string report)
        {
            var lastLine = report.Split('\n').Select(line => line.Trim().Trim('*', '`', '.', ' ')).LastOrDefault(line => line.Length > 0) ?? "";
            return lastLine == AllClaimsVerifiedMarker && !Regex.IsMatch(report, @"\b(WRONG|UNVERIFIED)\b");
        }

        private async Task<string> RunJobAsync(string task, string mode, string modeInstruction, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var emitEvent = ToolContext.EmitEvent;
            var handyTools = serviceProvider.GetServices<ICoreTool>()
                .Where(coreTool => HandyToolNames.Contains(coreTool.Name))
                .ToList();
            var toolDefinitions = BuildToolDefinitions(handyTools);
            var jobText = $"Job ({mode}): {task}\n\n{modeInstruction}";
            if (mode == "review")
            {
                // He can't see her system prompt, so a review gets Shapto's code style handed to it.
                var codeStyle = await Chat.SystemPrompt.LoadCodeStyleAsync(cancellationToken);
                if (codeStyle.Length > 0)
                {
                    jobText += "\n\nAlso check the code against Shapto's code style below, and list style problems separately from real bugs.\n\n" + codeStyle;
                }
            }

            var requestMessages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = jobText },
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

                var responseMessage = await lmStudioClient.StreamCompletionAsync(stepMessages, offerTools ? (JsonArray)toolDefinitions.DeepClone() : null, mode is "review" or "verify" ? ThinkingLevel.Code : ThinkingLevel.Light, (_, _) => Task.FromResult(true), cancellationToken);
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

        [GeneratedRegex(@"```[^\n]*\n(?<code>.*?)```", RegexOptions.Singleline)]
        private static partial Regex CodeFencePattern();

        // [Attribute], .Method( and Harmony's __special names.
        [GeneratedRegex(@"\[(?<name>[A-Z]\w+)\s*[\](,]|\.(?<name>[A-Za-z_]\w*)\s*\(|\b(?<name>__[a-z]\w*)")]
        private static partial Regex CodeNamePattern();

        [GeneratedRegex(@"(?<!`)`(?<code>[^`\n]+)`(?!`)")]
        private static partial Regex InlineCodePattern();

        // The name an inline code span starts with: [HarmonyArgument] → HarmonyArgument, RemoveAll(...) → RemoveAll, __result → __result.
        [GeneratedRegex(@"^\[?(?<name>[A-Za-z_]\w+)")]
        private static partial Regex InlineNamePattern();

        [GeneratedRegex(@"^Little Handy's report \(0 tool calls")]
        private static partial Regex ZeroToolCallsPattern();
    }
}