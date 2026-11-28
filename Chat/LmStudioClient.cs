using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhodes.Core.Engines;
using Rhodes.Core.Tools;

namespace Rhodes.Core.Chat;

/// <summary>
/// Talks to the active engine's OpenAI-compatible chat endpoint (llama-server or RustingStrata, see EngineManager).
/// Always streams, and logs timing and token counts for every request.
/// </summary>
public sealed class LmStudioClient
{
    public const string ReasoningDelta = "reasoning";
    public const string ContentDelta = "content";

    private readonly HttpClient httpClient;
    private readonly EngineManager engineManager;
    private readonly ModelManager modelManager;
    private readonly ILogger<LmStudioClient> logger;

    private const int MinimumTokensForSpeed = 16;

    // How often RustingStrata's log is checked for its prompt progress (it writes a line about every 2 s).
    private static readonly TimeSpan StrataProgressInterval = TimeSpan.FromSeconds(1);

    // RustingStrata's progress lines: "[12:03:41] prefill: 850.2 tok/s, 12000 tok" and "decode: 31.0 tok/s, 400 tok".
    private static readonly Regex StrataPrefillPattern = new(@"prefill: ([0-9.]+) tok/s, ([0-9]+) tok");
    private static readonly Regex StrataDecodePattern = new(@"decode: ([0-9.]+) tok/s");

    private const int LightThinkingBudget = 1000;
    private const int FullThinkingBudget = 4000;
    private const int CodeThinkingBudget = 8000;
    private const string ThinkingBudgetMessage = "\n\nOkay, I've thought about this enough. Time to answer.";

    /// <summary>
    /// Qwen3.6's recommended sampling for thinking mode (general tasks).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> ThinkingSampling = new Dictionary<string, double>
    {
        ["temperature"] = 1.0,
        ["top_p"] = 0.95,
        ["top_k"] = 20,
        ["min_p"] = 0.0,
        ["presence_penalty"] = 1.5,
        ["repeat_penalty"] = 1.0,
    };
    
    /// <summary>
    /// Qwen3.6's recommended sampling for thinking mode on precise coding tasks.
    /// No presence penalty: code repeats the same names on purpose, and a penalty pushes her to rename things and drift.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> CodeSampling = new Dictionary<string, double>
    {
        ["temperature"] = 0.6,
        ["top_p"] = 0.95,
        ["top_k"] = 20,
        ["min_p"] = 0.0,
        ["presence_penalty"] = 0.0,
        ["repeat_penalty"] = 1.0,
    };

    /// <summary>
    /// Qwen3.6's recommended sampling for non-thinking mode.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> InstantSampling = new Dictionary<string, double>
    {
        ["temperature"] = 0.7,
        ["top_p"] = 0.8,
        ["top_k"] = 20,
        ["min_p"] = 0.0,
        ["presence_penalty"] = 1.5,
        ["repeat_penalty"] = 1.0,
    };

    /// <summary>
    /// Thrown when a reply is stopped because thinking ran past the limit.
    /// </summary>
    public sealed class ThinkingLimitException(string message) : Exception(message);

    public double? LastTokensPerSecond { get; private set; }

    public LmStudioClient(EngineManager engineManager, ModelManager modelManager, string? apiKey, ILogger<LmStudioClient> logger)
    {
        // No base address: the URL follows the active engine (engineManager.BaseUrl) on every request.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        this.engineManager = engineManager;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        this.modelManager = modelManager;
        this.logger = logger;
    }

    /// <summary>
    /// Streams one completion. onDelta gets each piece of reasoning or content as it arrives
    /// and returns false to stop generation early. Returns the whole assistant message at the end.
    /// Pass null for toolDefinitions to force a plain-text answer.
    /// </summary>
    public async Task<JsonObject> StreamCompletionAsync(JsonArray messages, JsonArray? toolDefinitions, ThinkingLevel thinkingLevel, Func<string, string, Task<bool>> onDelta, CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            // The router picks the model by this name (its [section] in models.ini); a single-model llama-server ignores it.
            // RustingStrata gets the loaded file's name, so it never swaps models because of it.
            ["model"] = modelManager.RequestModelName,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (toolDefinitions is not null)
        {
            requestBody["tools"] = toolDefinitions;
        }
        // A model that sets its own sampling (rhodes.* in strata-models.ini) gets exactly that at every thinking level;
        // the presets are Qwen's recommendations and can push other models into loops (low temperature, no penalties).
        var sampling = modelManager.SamplingOverride ?? (thinkingLevel switch
        {
            ThinkingLevel.Off => InstantSampling,
            ThinkingLevel.Code => CodeSampling,
            _ => ThinkingSampling,
        });
        foreach (var (settingName, settingValue) in sampling)
        {
            requestBody[settingName] = settingValue;
        }

        if (thinkingLevel == ThinkingLevel.Off)
        {
            requestBody["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }
        else
        {
            var thinkingBudget = thinkingLevel switch
            {
                ThinkingLevel.Light => LightThinkingBudget,
                ThinkingLevel.Code => CodeThinkingBudget,
                _ => FullThinkingBudget,
            };
            if (engineManager.ThinkingStyle == ThinkingStyles.RustingStrata)
            {
                // RustingStrata ends the thinking block itself once thinking_budget tokens are used; it has no budget message.
                requestBody["thinking_budget"] = thinkingBudget;
            }
            else
            {
                requestBody["reasoning_budget_tokens"] = thinkingBudget;
                requestBody["reasoning_budget_message"] = ThinkingBudgetMessage;
            }
        }
        // While the prompt is read nothing streams, so the chat page would only say "waiting…". Both engines can tell
        // how far along they are: llama-server in the stream itself (return_progress), RustingStrata in its log.
        var emitEvent = ToolContext.EmitEvent;
        var isStrata = engineManager.IsStrata;
        if (emitEvent is not null && !isStrata)
        {
            requestBody["return_progress"] = true;
        }

        using var progressStop = new CancellationTokenSource();
        var strataProgressTask = emitEvent is not null && isStrata && engineManager.CurrentEngine is { } strataEngine
            ? ReportStrataProgressAsync(strataEngine.LogPath, emitEvent, progressStop.Token)
            : Task.CompletedTask;

        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, engineManager.BaseUrl + "/chat/completions")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        try
        {
            return await ReadCompletionAsync(request, stopwatch, onDelta, emitEvent, progressStop, cancellationToken);
        }
        finally
        {
            await progressStop.CancelAsync();
            await strataProgressTask;
        }
    }

    private async Task<JsonObject> ReadCompletionAsync(HttpRequestMessage request, Stopwatch stopwatch, Func<string, string, Task<bool>> onDelta, Func<ChatEvent, Task>? emitEvent, CancellationTokenSource progressStop, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Model server returned {(int)response.StatusCode}: {errorText}");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);

        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var toolCallParts = new SortedDictionary<int, ToolCallParts>();
        JsonNode? usage = null;
        JsonNode? timings = null;
        double? firstTokenSeconds = null;
        var keepReading = true;

        while (keepReading && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            var chunk = JsonNode.Parse(payload);
            usage = chunk?["usage"] ?? usage;
            timings = chunk?["timings"] ?? timings;
            if (chunk?["prompt_progress"] is JsonObject promptProgress && emitEvent is not null && firstTokenSeconds is null)
            {
                await EmitSafelyAsync(emitEvent, DescribeLlamaProgress(promptProgress));
            }

            var choices = chunk?["choices"] as JsonArray;
            var delta = choices is { Count: > 0 } ? choices[0]?["delta"] : null;
            if (delta is null)
            {
                continue;
            }

            if (firstTokenSeconds is null)
            {
                firstTokenSeconds = stopwatch.Elapsed.TotalSeconds;
                // The model is writing now: the page's thinking/writing status takes over from the prompt progress.
                await progressStop.CancelAsync();
            }

            var reasoningText = GetString(delta["reasoning_content"]) ?? GetString(delta["reasoning"]);
            if (!string.IsNullOrEmpty(reasoningText))
            {
                reasoningBuilder.Append(reasoningText);
                keepReading = await onDelta(ReasoningDelta, reasoningText);
            }

            var contentText = GetString(delta["content"]);
            if (keepReading && !string.IsNullOrEmpty(contentText))
            {
                contentBuilder.Append(contentText);
                keepReading = await onDelta(ContentDelta, contentText);
            }

            if (delta["tool_calls"] is JsonArray toolCallDeltas)
            {
                AccumulateToolCalls(toolCallParts, toolCallDeltas);
            }
        }

        logger.LogInformation(
            "Completion in {Seconds:F1} s (first token {FirstTokenSeconds:F1} s): {PromptTokens} prompt tokens ({CachedTokens} cached), {CompletionTokens} generated{StoppedNote}",
            stopwatch.Elapsed.TotalSeconds,
            firstTokenSeconds,
            usage?["prompt_tokens"],
            usage?["prompt_tokens_details"]?["cached_tokens"],
            usage?["completion_tokens"],
            keepReading ? "" : ", stopped early");

        var tokensPerSecond = RecordSpeed(usage, timings, stopwatch.Elapsed.TotalSeconds, firstTokenSeconds);

        var message = BuildMessage(contentBuilder, reasoningBuilder, toolCallParts);
        message["rhodes_prompt_tokens"] = usage?["prompt_tokens"]?.DeepClone();
        message["rhodes_completion_tokens"] = usage?["completion_tokens"]?.DeepClone();
        message["rhodes_cached_tokens"] = usage?["prompt_tokens_details"]?["cached_tokens"]?.DeepClone();
        message["rhodes_tokens_per_second"] = tokensPerSecond;
        return message;
    }

    /// <summary>
    /// llama-server's prompt_progress: total prompt tokens, how many came from the cache, how many are done so far.
    /// </summary>
    private static string DescribeLlamaProgress(JsonObject promptProgress)
    {
        var totalTokens = GetDouble(promptProgress["total"]) ?? 0;
        var cachedTokens = GetDouble(promptProgress["cache"]) ?? 0;
        var processedTokens = GetDouble(promptProgress["processed"]) ?? 0;
        var elapsedMilliseconds = GetDouble(promptProgress["time_ms"]) ?? 0;
        var newTokens = Math.Max(0, totalTokens - cachedTokens);
        var doneTokens = Math.Clamp(processedTokens - cachedTokens, 0, newTokens);
        var speedText = elapsedMilliseconds > 0 && doneTokens > 0 ? $" at {doneTokens / (elapsedMilliseconds / 1000):0} tok/s" : "";
        var cachedText = cachedTokens > 0 ? $" ({cachedTokens:N0} cached)" : "";
        return $"reading the prompt · {doneTokens:N0} / {newTokens:N0} tok{speedText}{cachedText}";
    }

    /// <summary>
    /// Follows RustingStrata's log until the first token: its prefill lines say how much of the prompt it has read.
    /// Only lines written after the request started count, so the end of the previous request isn't mistaken for this one.
    /// </summary>
    private static async Task ReportStrataProgressAsync(string logPath, Func<ChatEvent, Task> emitEvent, CancellationToken stopToken)
    {
        var startingLine = EngineManager.ReadLastLogLine(logPath);
        string? reportedLine = null;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                await Task.Delay(StrataProgressInterval, stopToken);
                var lastLine = EngineManager.ReadLastLogLine(logPath);
                if (lastLine is null || lastLine == startingLine || lastLine == reportedLine)
                {
                    continue;
                }

                reportedLine = lastLine;
                if (StrataPrefillPattern.Match(lastLine) is { Success: true } prefillMatch)
                {
                    var tokensRead = long.Parse(prefillMatch.Groups[2].Value);
                    var tokensPerSecond = double.Parse(prefillMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    await EmitSafelyAsync(emitEvent, $"reading the prompt · {tokensRead:N0} tok at {tokensPerSecond:0} tok/s");
                }
                else if (StrataDecodePattern.IsMatch(lastLine))
                {
                    // Writing before this request's first token: RustingStrata is still answering an earlier request
                    // (a side request like the answer check or titling), and this one waits its turn.
                    await EmitSafelyAsync(emitEvent, "waiting for the model · it's finishing another request");
                }
                else if (lastLine.Contains("request:", StringComparison.Ordinal))
                {
                    await EmitSafelyAsync(emitEvent, "the model took the request · reading the prompt…");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The first token arrived or the request ended.
        }
    }

    private static async Task EmitSafelyAsync(Func<ChatEvent, Task> emitEvent, string statusText)
    {
        try
        {
            await emitEvent(new ChatEvent("status", statusText));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The page went away; the turn goes on without it.
        }
    }

    /// <summary>
    /// Uses llama-server's own measurement when it sends one; otherwise generated tokens over the time after the first token.
    /// Returns this request's speed, or null if it generated too little to measure.
    /// </summary>
    private double? RecordSpeed(JsonNode? usage, JsonNode? timings, double totalSeconds, double? firstTokenSeconds)
    {
        var completionTokens = GetDouble(usage?["completion_tokens"]);
        if (completionTokens is null || completionTokens < MinimumTokensForSpeed)
        {
            return null;
        }

        var generationSeconds = totalSeconds - (firstTokenSeconds ?? 0);
        var measuredSpeed = GetDouble(timings?["predicted_per_second"]);
        var tokensPerSecond = measuredSpeed ?? (generationSeconds > 0 ? completionTokens / generationSeconds : null);
        LastTokensPerSecond = tokensPerSecond ?? LastTokensPerSecond;
        return tokensPerSecond;
    }

    private static double? GetDouble(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    /// <summary>
    /// Tool calls arrive in pieces: the name once, the arguments a few characters at a time, keyed by index.
    /// </summary>
    private static void AccumulateToolCalls(SortedDictionary<int, ToolCallParts> toolCallParts, JsonArray toolCallDeltas)
    {
        foreach (var toolCallDelta in toolCallDeltas)
        {
            var index = toolCallDelta?["index"]?.GetValue<int>() ?? toolCallParts.Count;
            if (!toolCallParts.TryGetValue(index, out var parts))
            {
                parts = new ToolCallParts();
                toolCallParts[index] = parts;
            }

            parts.Id ??= GetString(toolCallDelta?["id"]);
            parts.Name.Append(GetString(toolCallDelta?["function"]?["name"]));
            parts.Arguments.Append(GetString(toolCallDelta?["function"]?["arguments"]));
        }
    }

    private static JsonObject BuildMessage(StringBuilder contentBuilder, StringBuilder reasoningBuilder, SortedDictionary<int, ToolCallParts> toolCallParts)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = contentBuilder.ToString(),
            ["reasoning_content"] = reasoningBuilder.ToString(),
        };

        if (toolCallParts.Count > 0)
        {
            var toolCalls = new JsonArray();
            foreach (var (index, parts) in toolCallParts)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = parts.Id ?? $"call_{index}",
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = parts.Name.ToString(),
                        ["arguments"] = parts.Arguments.ToString(),
                    },
                });
            }

            message["tool_calls"] = toolCalls;
        }

        return message;
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private sealed class ToolCallParts
    {
        public string? Id { get; set; }

        public StringBuilder Name { get; } = new();

        public StringBuilder Arguments { get; } = new();
    }
}
