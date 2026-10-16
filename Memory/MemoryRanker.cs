namespace Rhodes.Core.Memory
{
    /// <summary>
    /// A memory with the scores that ranked it, kept apart so the client can later show why it surfaced.
    /// </summary>
    public sealed record RankedMemory(MemoryRecord Memory, double Similarity, double Recency, double Frequency, double CompositeScore);

    /// <summary>
    /// Ports the khtsly plugin's ranking: keyword match, recency, use count and confidence
    /// blended into one score, with the same weights.
    /// </summary>
    public sealed class MemoryRanker
    {
        private const double SimilarityWeight = 0.55;
        private const double RecencyWeight = 0.20;
        private const double FrequencyWeight = 0.10;
        private const double ConfidenceWeight = 0.15;
        private const double MinimumSimilarity = 0.25;
        private const int MaximumCandidates = 100;

        private readonly MemoryStore memoryStore;
        private readonly double decayHalfLifeDays;

        public MemoryRanker(MemoryStore memoryStore, double decayHalfLifeDays)
        {
            this.memoryStore = memoryStore;
            this.decayHalfLifeDays = decayHalfLifeDays;
        }

        /// <summary>
        /// Finds and ranks memories for a query. countAsAccess is true for deliberate searches
        /// and false for auto-inject, so injection doesn't inflate use counts.
        /// </summary>
        public IReadOnlyList<RankedMemory> Rank(string query, int limit, bool includeArchive, bool countAsAccess)
        {
            var candidateLimit = Math.Min(limit * 3, MaximumCandidates);
            var hits = memoryStore.Search(query, candidateLimit, includeArchive);
            if (hits.Count == 0)
            {
                return [];
            }

            var bestKeywordScore = hits.Max(hit => hit.KeywordScore);
            var maximumAccessCount = memoryStore.GetMaximumAccessCount();
            var now = DateTimeOffset.UtcNow;

            var rankedMemories = hits
                .Select(hit => Score(hit, bestKeywordScore, maximumAccessCount, now))
                .Where(rankedMemory => rankedMemory.Similarity >= MinimumSimilarity)
                .OrderByDescending(rankedMemory => rankedMemory.CompositeScore)
                .Take(limit)
                .ToList();

            if (countAsAccess && rankedMemories.Count > 0)
            {
                memoryStore.TouchAccess(rankedMemories.Select(rankedMemory => rankedMemory.Memory.Id));
            }

            return rankedMemories;
        }

        private RankedMemory Score(MemorySearchHit hit, double bestKeywordScore, long maximumAccessCount, DateTimeOffset now)
        {
            var similarity = bestKeywordScore > 0 ? hit.KeywordScore / bestKeywordScore : 0.0;
            var daysSinceAccess = Math.Max(0.0, (now - hit.Memory.LastAccessedAt).TotalDays);
            var recency = Math.Pow(2.0, -daysSinceAccess / decayHalfLifeDays);
            var frequency = maximumAccessCount > 0 ? Math.Log(1 + hit.Memory.AccessCount) / Math.Log(1 + maximumAccessCount) : 0.0;
            var compositeScore = SimilarityWeight * similarity + RecencyWeight * recency + FrequencyWeight * frequency + ConfidenceWeight * hit.Memory.Confidence;
            return new RankedMemory(hit.Memory, similarity, recency, frequency, compositeScore);
        }
    }
}
