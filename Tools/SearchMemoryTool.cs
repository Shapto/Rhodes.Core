using Rhodes.Core.Memory;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Deliberate memory search. Archive memories are included here (unlike auto-inject),
    /// and only the memories actually returned count as used.
    /// </summary>
    public sealed class SearchMemoryTool : ICoreTool
    {
        private const int MaximumContentCharacters = 400;

        private readonly MemoryRanker memoryRanker;
        private readonly MemoryStore memoryStore;

        public SearchMemoryTool(MemoryRanker memoryRanker, MemoryStore memoryStore)
        {
            this.memoryRanker = memoryRanker;
            this.memoryStore = memoryStore;
        }

        public string Name => "search_memory";

        public string Description =>
        "Search your long-term memory of Shapto by keywords. Use when they refer to something from before, " +
        "or you need a fact about them or their projects that isn't in this conversation. " +
        "Not for general knowledge or anything current; use web_search for that.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Keywords to look for, e.g. \"Shin and Mang release\".",
                },
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("fact", "preference", "project", "event", "persona", "archive"),
                    ["description"] = "Only return this type. Leave out to search everything.",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 10,
                    ["description"] = "How many to return. Default 5.",
                },
            },
            ["required"] = new JsonArray("query"),
        };

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var query = ToolArguments.GetRequiredString(arguments, "query");
            var typeFilter = ToolArguments.GetOptionalString(arguments, "type")?.Trim().ToLowerInvariant();
            var limit = ToolArguments.GetOptionalInteger(arguments, "limit", defaultValue: 5, minimum: 1, maximum: 10);

            if (typeFilter is not null && !MemoryTypes.All.Contains(typeFilter))
            {
                throw new ToolArgumentException($"Unknown type \"{typeFilter}\". Use one of: {string.Join(", ", MemoryTypes.All)}, or leave type out.");
            }

            var candidateCount = typeFilter is null ? limit : limit * 4;
            var foundMemories = memoryRanker.Rank(query, candidateCount, includeArchive: true, countAsAccess: false)
                .Select(rankedMemory => rankedMemory.Memory)
                .Where(memory => typeFilter is null || memory.Type == typeFilter)
                .Take(limit)
                .ToList();

            if (foundMemories.Count == 0)
            {
                return Task.FromResult($"No memories matched \"{query}\". Try other keywords (names, project names), or ask Shapto.");
            }

            memoryStore.TouchAccess(foundMemories.Select(memory => memory.Id));

            var builder = new StringBuilder();
            builder.AppendLine($"Found {foundMemories.Count} memories for \"{query}\":");
            foreach (var memory in foundMemories)
            {
                var shownContent = (memory.Content.Length > MaximumContentCharacters ? memory.Content[..MaximumContentCharacters] + "…" : memory.Content).Replace("<think>", "[think tag]").Replace("</think>", "[think tag]");
                builder.AppendLine($"- [{memory.Type}, saved {memory.CreatedAt:yyyy-MM-dd}, id {memory.Id}] {shownContent}");
            }

            return Task.FromResult(builder.ToString());
        }
    }
}
