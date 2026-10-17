using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Chat;

/// <summary>
/// Talks to any OpenAI-compatible chat endpoint: LM Studio now, llama-server later.
/// Always streams, and logs timing and token counts for every request.
/// </summary>
public sealed class LmStudioClient
{
    public const string ReasoningDelta = "reasoning";
    public const string ContentDelta = "content";

    private readonly HttpClient httpClient;
    private readonly string modelIdentifier;
    private readonly ILogger<LmStudioClient> logger;

    private const int LightThinkingBudget = 1000;
    private const int FullThinkingBudget = 4000;
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

    public LmStudioClient(string baseUrl, string modelIdentifier, string? apiKey, ILogger<LmStudioClient> logger)
    {
        httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        this.modelIdentifier = modelIdentifier;
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
            ["model"] = modelIdentifier,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (toolDefinitions is not null)
        {
            requestBody["tools"] = toolDefinitions;
        }
        var sampling = thinkingLevel == ThinkingLevel.Off ? InstantSampling : ThinkingSampling;
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
            requestBody["reasoning_budget_tokens"] = thinkingLevel == ThinkingLevel.Light ? LightThinkingBudget : FullThinkingBudget;
            requestBody["reasoning_budget_message"] = ThinkingBudgetMessage;
        }
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };
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
            var choices = chunk?["choices"] as JsonArray;
            var delta = choices is { Count: > 0 } ? choices[0]?["delta"] : null;
            if (delta is null)
            {
                continue;
            }

            firstTokenSeconds ??= stopwatch.Elapsed.TotalSeconds;

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

        var message = BuildMessage(contentBuilder, reasoningBuilder, toolCallParts);
        message["rhodes_prompt_tokens"] = usage?["prompt_tokens"]?.DeepClone();
        message["rhodes_completion_tokens"] = usage?["completion_tokens"]?.DeepClone();
        return message;
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
