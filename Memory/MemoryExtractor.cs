using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhodes.Core.Chat;

namespace Rhodes.Core.Memory
{
    /// <summary>
    /// What one extraction did: the contents it added, and the contents that replaced an older version.
    /// </summary>
    public sealed record ExtractionResult(IReadOnlyList<string> AddedContents, IReadOnlyList<string> UpdatedContents);

    /// <summary>
    /// Files durable memories from a stretch of chat, the way the design doc's background pass describes:
    /// only what Shapto said, at the strength it was said, and only what will still matter in a month.
    /// Used after a chat goes quiet and right before compaction folds messages into the summary.
    /// The model proposes; this class checks every proposal (type, length, duplicates) before anything is saved.
    /// </summary>
    public sealed partial class MemoryExtractor
    {
        private const int MaximumTranscriptCharacters = 40_000;
        private const int MaximumMessageCharacters = 1500;
        private const int RelatedMemoryCount = 12;
        private const int MinimumContentCharacters = 12;
        private const int MaximumContentCharacters = 500;
        private const int MaximumSavesPerPass = 8;
        private const double DuplicateWordOverlap = 0.8;
        private const string Source = "background";

        private static readonly HashSet<string> SavableTypes = [MemoryTypes.Fact, MemoryTypes.Preference, MemoryTypes.Project, MemoryTypes.Event, MemoryTypes.Persona];

        private const string Instructions = """
            You file long-term memories for Closure, an AI who works with Shapto (they/them). You read part of a chat and decide what's worth remembering.

            Rules:
            - Only what Shapto said about themselves, their work, their projects, their preferences, people in their life, or decisions they made. Never what Closure said, guessed or looked up, never web or tool content.
            - At the strength it was said: one mention is "mentioned once", not a passion. No conclusions or guesses about Shapto.
            - The horizon test: would this still be true and useful a month from now, in a conversation about something else? Skip moods, today's tasks, small talk, one-off questions.
            - Stable facts (people, projects, roles, decisions) count on first mention. Passing remarks don't.
            - If an existing memory is now out of date, update it ("moved from X to Y") instead of adding a contradicting one. Never add what an existing memory already says.
            - No health, money, or other sensitive details. Nothing about thinking tags or how Closure formats her reasoning.
            - Most chats contain nothing worth filing. An empty list is a good answer.

            Types: fact (about Shapto or their world), preference (how they want things done), project (their projects and decisions in them), event (something that happened, with its date), persona (about Closure herself, only if Shapto said it).

            Reply with only JSON, no other text:
            {"memories": [
              {"action": "add", "type": "project", "content": "one full sentence", "tags": ["short", "tags"]},
              {"action": "update", "replaces": "<id of the existing memory>", "type": "fact", "content": "the corrected full sentence", "tags": []}
            ]}
            """;

        private readonly LmStudioClient lmStudioClient;
        private readonly MemoryStore memoryStore;
        private readonly MemoryRanker memoryRanker;
        private readonly ILogger<MemoryExtractor> logger;

        public MemoryExtractor(LmStudioClient lmStudioClient, MemoryStore memoryStore, MemoryRanker memoryRanker, ILogger<MemoryExtractor> logger)
        {
            this.lmStudioClient = lmStudioClient;
            this.memoryStore = memoryStore;
            this.memoryRanker = memoryRanker;
            this.logger = logger;
        }

        /// <summary>
        /// Reads messages [fromIndex, toIndex) of a chat and saves what passes the rules. Turns where Shapto explicitly asked to
        /// remember or forget something are left out: those were handled in the turn itself, and re-saving could undo a "forget".
        /// </summary>
        public async Task<ExtractionResult> ExtractAsync(Conversation conversation, int fromIndex, int toIndex, CancellationToken cancellationToken)
        {
            var transcript = BuildTranscript(conversation, fromIndex, toIndex);
            if (transcript.Length == 0)
            {
                return new ExtractionResult([], []);
            }

            var relatedMemories = memoryRanker.Rank(Shorten(transcript, 4000), RelatedMemoryCount, includeArchive: false, countAsAccess: false)
                .Select(rankedMemory => rankedMemory.Memory)
                .ToList();
            var existingText = relatedMemories.Count == 0 ? "(none)" : string.Join("\n", relatedMemories.Select(memory => $"[{memory.Id}] {memory.Type}: {memory.Content}"));
            var requestMessages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject { ["role"] = "user", ["content"] = $"Existing memories that may be related:\n{existingText}\n\nThe chat:\n{transcript}" },
            };

            var responseMessage = await lmStudioClient.StreamCompletionAsync(requestMessages, null, ThinkingLevel.Off, (_, _) => Task.FromResult(true), cancellationToken);
            var (_, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), null);
            var proposals = ParseProposals(answer);

            var added = new List<string>();
            var updated = new List<string>();
            var relatedIds = relatedMemories.Select(memory => memory.Id).ToHashSet();
            foreach (var proposal in proposals.Take(MaximumSavesPerPass))
            {
                var content = proposal.Content.Trim();
                if (!SavableTypes.Contains(proposal.Type) || content.Length < MinimumContentCharacters || content.Length > MaximumContentCharacters || ThinkTagPattern().IsMatch(content))
                {
                    continue;
                }

                // An update may only replace a memory she was shown; anything else is treated as an add.
                if (proposal.Action == "update" && proposal.Replaces is { } replacedId && relatedIds.Contains(replacedId))
                {
                    memoryStore.Supersede([replacedId], content, proposal.Type, proposal.Tags, Source);
                    relatedIds.Remove(replacedId);
                    updated.Add(content);
                    continue;
                }

                if (IsNearDuplicate(content))
                {
                    continue;
                }

                memoryStore.Save(content, proposal.Type, proposal.Tags, 1.0, Source);
                added.Add(content);
            }

            logger.LogInformation("Memory pass on chat {ConversationId} (messages {FromIndex}–{ToIndex}): {Added} added, {Updated} updated", conversation.Id, fromIndex, toIndex - 1, added.Count, updated.Count);
            return new ExtractionResult(added, updated);
        }

        /// <summary>
        /// Shapto's words and Closure's answers, oldest first, with their dates. Turns with an explicit remember or forget are skipped.
        /// </summary>
        private static string BuildTranscript(Conversation conversation, int fromIndex, int toIndex)
        {
            var lines = new List<string>();
            var skipTurn = false;
            for (var index = Math.Max(0, fromIndex); index < Math.Min(toIndex, conversation.Messages.Count); index++)
            {
                var message = conversation.Messages[index];
                var role = GetString(message["role"]);
                if (role == "user")
                {
                    var userText = GetString(message["rhodes_display_text"]) ?? "";
                    skipTurn = ExplicitMemoryRequestPattern().IsMatch(userText) || TurnSavedMemory(conversation, index);
                    if (!skipTurn && userText.Trim().Length > 0)
                    {
                        lines.Add($"Shapto{DescribeDate(GetString(message["rhodes_sent_at"]))}: {Shorten(userText, MaximumMessageCharacters)}");
                    }
                }
                else if (role == "assistant" && !skipTurn && message["tool_calls"] is null && GetString(message["content"]) is { Length: > 0 } answer)
                {
                    lines.Add($"Closure: {Shorten(answer, MaximumMessageCharacters)}");
                }
            }

            var transcript = string.Join("\n", lines);
            return transcript.Length > MaximumTranscriptCharacters ? transcript[^MaximumTranscriptCharacters..] : transcript;
        }

        private static bool TurnSavedMemory(Conversation conversation, int userIndex)
        {
            for (var index = userIndex + 1; index < conversation.Messages.Count && GetString(conversation.Messages[index]["role"]) != "user"; index++)
            {
                if (GetString(conversation.Messages[index]["rhodes_tool_name"]) == "save_memory")
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsNearDuplicate(string content)
        {
            var contentWords = WordPattern().Matches(content.ToLowerInvariant()).Select(match => match.Value).ToHashSet();
            if (contentWords.Count == 0)
            {
                return true;
            }

            return memoryRanker.Rank(content, limit: 5, includeArchive: false, countAsAccess: false)
                .Select(rankedMemory => WordPattern().Matches(rankedMemory.Memory.Content.ToLowerInvariant()).Select(match => match.Value).ToHashSet())
                .Any(memoryWords =>
                {
                    var sharedCount = contentWords.Count(word => memoryWords.Contains(word));
                    var unionCount = contentWords.Count + memoryWords.Count - sharedCount;
                    return unionCount > 0 && (double)sharedCount / unionCount >= DuplicateWordOverlap;
                });
        }

        private sealed record Proposal(string Action, string Type, string Content, IReadOnlyList<string> Tags, string? Replaces);

        /// <summary>
        /// The JSON object in her reply, tolerating text or code fences around it. Anything unreadable means no proposals.
        /// </summary>
        private static List<Proposal> ParseProposals(string answer)
        {
            var proposals = new List<Proposal>();
            var startIndex = answer.IndexOf('{');
            var endIndex = answer.LastIndexOf('}');
            if (startIndex < 0 || endIndex <= startIndex)
            {
                return proposals;
            }

            try
            {
                if (JsonNode.Parse(answer[startIndex..(endIndex + 1)])?["memories"] is not JsonArray memoryNodes)
                {
                    return proposals;
                }

                foreach (var memoryNode in memoryNodes)
                {
                    var tags = (memoryNode?["tags"] as JsonArray ?? [])
                        .Select(tagNode => GetString(tagNode))
                        .OfType<string>()
                        .ToList();
                    proposals.Add(new Proposal(
                        GetString(memoryNode?["action"])?.ToLowerInvariant() ?? "add",
                        GetString(memoryNode?["type"])?.ToLowerInvariant() ?? "",
                        GetString(memoryNode?["content"]) ?? "",
                        tags,
                        GetString(memoryNode?["replaces"])));
                }
            }
            catch (JsonException)
            {
                // A malformed reply files nothing; the next pass gets another chance.
            }

            return proposals;
        }

        private static string DescribeDate(string? sentAtText)
        {
            return DateTimeOffset.TryParse(sentAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sentAt) ? " (" + sentAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")" : "";
        }

        private static string Shorten(string text, int maximumCharacters)
        {
            return text.Length > maximumCharacters ? text[..maximumCharacters] + "…" : text;
        }

        private static string? GetString(JsonNode? node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        [GeneratedRegex(@"\b(remember (this|that|it)|don'?t forget|forget (this|that|it|about)|delete (this|that|the) memory)\b", RegexOptions.IgnoreCase)]
        private static partial Regex ExplicitMemoryRequestPattern();

        [GeneratedRegex(@"think>|<think", RegexOptions.IgnoreCase)]
        private static partial Regex ThinkTagPattern();

        [GeneratedRegex(@"[\p{L}\p{N}_]+")]
        private static partial Regex WordPattern();
    }
}