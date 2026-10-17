using global::Rhodes.Core.Memory;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Rhodes.Core.Tools
{


    /// <summary>
    /// Saves one memory at a time, refusing near-duplicates and overly long entries.
    /// Archive isn't offered: only Shapto files things as archive.
    /// </summary>
    public sealed class SaveMemoryTool : ICoreTool
    {
        private const int MinimumContentCharacters = 5;
        private const int MaximumContentCharacters = 500;
        private const double DuplicateWordOverlap = 0.8;

        private static readonly HashSet<string> SavableTypes = ["fact", "preference", "project", "event", "persona"];

        private readonly MemoryStore memoryStore;
        private readonly MemoryRanker memoryRanker;

        public SaveMemoryTool(MemoryStore memoryStore, MemoryRanker memoryRanker)
        {
            this.memoryStore = memoryStore;
            this.memoryRanker = memoryRanker;
        }

        public string Name => "save_memory";

        public string Description =>
        "Save something Shapto told you that will still matter in a month: facts about them, their projects, " +
        "preferences, decisions. Only what they actually said, at the strength they said it. " +
        "Not for your own guesses, web results, or details of the current task.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One fact in one sentence, e.g. \"Shapto released Shin and Mang on the Workshop.\"",
                },
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("fact", "preference", "project", "event", "persona"),
                    ["description"] = "fact, preference, project, event (something that happened, with a date), or persona (about yourself).",
                },
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Optional keywords, e.g. [\"rimworld\", \"mod\"].",
                },
            },
            ["required"] = new JsonArray("content", "type"),
        };

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var content = ToolArguments.GetRequiredString(arguments, "content").Trim();
            var type = (ToolArguments.GetOptionalString(arguments, "type") ?? MemoryTypes.Fact).Trim().ToLowerInvariant();
            var tags = ToolArguments.GetOptionalStringList(arguments, "tags");

            if (!SavableTypes.Contains(type))
            {
                throw new ToolArgumentException($"Type must be one of: {string.Join(", ", SavableTypes)}.");
            }

            if (content.Contains("<think", StringComparison.OrdinalIgnoreCase) || content.Contains("think>", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolArgumentException("Don't save notes about your own reasoning format; Rhodes handles that.");
            }

            if (content.Length < MinimumContentCharacters)
            {
                throw new ToolArgumentException("Content is too short to be useful later. Write the fact as a full sentence.");
            }

            if (content.Length > MaximumContentCharacters)
            {
                throw new ToolArgumentException($"Content is {content.Length} characters; the limit is {MaximumContentCharacters}. Save one fact per memory and split the rest into separate saves.");
            }

            var existingMemory = FindNearDuplicate(content);
            if (existingMemory is not null)
            {
                return Task.FromResult($"Not saved: already remembered as {existingMemory.Type} (id {existingMemory.Id}): {existingMemory.Content}");
            }

            var memoryId = memoryStore.Save(content, type, tags, confidence: 1.0, source: "closure");
            return Task.FromResult($"Saved as {type} (id {memoryId}).");
        }

        private MemoryRecord? FindNearDuplicate(string content)
        {
            var contentWords = GetWordSet(content);
            if (contentWords.Count == 0)
            {
                return null;
            }

            return memoryRanker.Rank(content, limit: 5, includeArchive: false, countAsAccess: false)
                .Select(rankedMemory => rankedMemory.Memory)
                .FirstOrDefault(memory => GetWordOverlap(contentWords, GetWordSet(memory.Content)) >= DuplicateWordOverlap);
        }

        private static HashSet<string> GetWordSet(string text)
        {
            return Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_]+")
                .Select(match => match.Value)
                .ToHashSet();
        }

        /// <summary>
        /// Shared words divided by all distinct words in both texts: 1.0 means the same words.
        /// </summary>
        private static double GetWordOverlap(HashSet<string> firstWords, HashSet<string> secondWords)
        {
            var sharedCount = firstWords.Count(word => secondWords.Contains(word));
            var unionCount = firstWords.Count + secondWords.Count - sharedCount;
            return unionCount == 0 ? 0.0 : (double)sharedCount / unionCount;
        }
    }
}
