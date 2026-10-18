using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Rhodes.Core.Chat;
using Rhodes.Core.Heartbeat;

namespace Rhodes.Core.Memory
{
    /// <summary>
    /// One change Closure proposes for her memory. Kind is merge, archive, retype or delete; nothing happens until Shapto accepts it.
    /// Status is open, accepted, rejected or stale (a memory it names is gone or already changed).
    /// </summary>
    public sealed record MemoryProposal(long Id, string Kind, IReadOnlyList<string> MemoryIds, string? NewContent, string? NewType, string Reason, string Status, DateTimeOffset CreatedAt);

    public sealed record MemoryProposalView(MemoryProposal Proposal, IReadOnlyList<MemoryRecord> Memories);

    public sealed record MemoryReviewStatus(bool Running, string Progress, DateTimeOffset? LastRunAt, string? LastError, int OpenCount);

    public sealed record ProposalDecision(string Decision);

    /// <summary>
    /// The cleanup review the design doc asks for: Closure reads her memories in batches and proposes merges for duplicates,
    /// archives for what's outdated or contradicted by something newer, retypes, and (rarely) deletes for what's false.
    /// Proposals wait on the dashboard for Shapto; accepting one applies it, with merges recorded through supersedes.
    /// </summary>
    public sealed class MemoryReviewer : IDisposable
    {
        private const int BatchSize = 40;
        private const int MaximumProposalsPerBatch = 10;
        private static readonly HashSet<string> Kinds = ["merge", "archive", "retype", "delete"];

        private const string Instructions = """
            You review the long-term memories of Closure, an AI who works with Shapto (they/them), and propose cleanups. Shapto approves each one.

            Propose only clear improvements:
            - merge: two or more memories say the same thing or overlap a lot. Give one combined full sentence that keeps every detail.
            - archive: a memory is outdated, replaced by a newer memory in the list, contradicted by a newer one (archive the older), or no longer useful. Archived memories stay searchable.
            - retype: the type is wrong. Types: fact, preference, project, event, persona.
            - delete: only when a memory is false or harmful. When unsure, archive instead.
            Leave everything else alone. Few good proposals beat many weak ones; an empty list is fine. Give each a short reason.

            Reply with only JSON, no other text:
            {"proposals": [
              {"kind": "merge", "ids": ["id1", "id2"], "content": "the combined sentence", "type": "project", "reason": "same fact saved twice"},
              {"kind": "archive", "ids": ["id3"], "reason": "contradicted by id4, which is newer"},
              {"kind": "retype", "ids": ["id5"], "type": "preference", "reason": "it's how Shapto wants code written"},
              {"kind": "delete", "ids": ["id6"], "reason": "false: ..."}
            ]}
            """;

        private readonly SqliteConnection connection;
        private readonly object databaseLock = new();
        private readonly MemoryStore memoryStore;
        private readonly LmStudioClient lmStudioClient;
        private readonly ActivityTracker activityTracker;
        private readonly ModelStatusMonitor modelStatusMonitor;
        private readonly HeartbeatRunner heartbeatRunner;
        private readonly ILogger<MemoryReviewer> logger;
        private int running;
        private string progress = "";
        private DateTimeOffset? lastRunAt;
        private string? lastError;

        public MemoryReviewer(string databasePath, MemoryStore memoryStore, LmStudioClient lmStudioClient, ActivityTracker activityTracker, ModelStatusMonitor modelStatusMonitor, HeartbeatRunner heartbeatRunner, ILogger<MemoryReviewer> logger)
        {
            this.memoryStore = memoryStore;
            this.lmStudioClient = lmStudioClient;
            this.activityTracker = activityTracker;
            this.modelStatusMonitor = modelStatusMonitor;
            this.heartbeatRunner = heartbeatRunner;
            this.logger = logger;
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            connection = new SqliteConnection(connectionString);
            connection.Open();
            Execute("PRAGMA journal_mode = WAL;");
            Execute("PRAGMA busy_timeout = 5000;");
            Execute("""
            CREATE TABLE IF NOT EXISTS memory_proposals (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                kind         TEXT NOT NULL,
                memory_ids   TEXT NOT NULL,
                new_content  TEXT,
                new_type     TEXT,
                reason       TEXT NOT NULL,
                status       TEXT NOT NULL DEFAULT 'open',
                created_at   INTEGER NOT NULL,
                decided_at   INTEGER
            );
            """);
        }

        public MemoryReviewStatus GetStatus()
        {
            return new MemoryReviewStatus(Volatile.Read(ref running) == 1, progress, lastRunAt, lastError, ListProposals("open").Count);
        }

        /// <summary>
        /// Starts a review in the background. Returns false if one is already running.
        /// </summary>
        public bool Start()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    lastError = null;
                    await RunAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                    logger.LogWarning(exception, "Memory review failed");
                }
                finally
                {
                    lastRunAt = DateTimeOffset.Now;
                    progress = "";
                    Volatile.Write(ref running, 0);
                }
            });
            return true;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // Memories already named by an open proposal wait for Shapto's decision before being reviewed again.
            var alreadyProposed = ListProposals("open").SelectMany(proposal => proposal.MemoryIds).ToHashSet();
            var memories = memoryStore.ListForReview()
                .Where(memory => !alreadyProposed.Contains(memory.Id))
                .ToList();
            var batches = memories.Chunk(BatchSize).ToList();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                progress = $"batch {batchIndex + 1} of {batches.Count}";
                await WaitForQuietModelAsync(cancellationToken);
                await ReviewBatchAsync(batches[batchIndex], cancellationToken);
            }

            await heartbeatRunner.WarmUpLatestChatAsync(cancellationToken);
        }

        /// <summary>
        /// A chat always goes first: the review only uses the model between Shapto's messages.
        /// </summary>
        private async Task WaitForQuietModelAsync(CancellationToken cancellationToken)
        {
            while (activityTracker.IsChatRunning || modelStatusMonitor.Current.State != ModelStates.Online)
            {
                progress = progress.Split(" · ")[0] + " · waiting for the model";
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            progress = progress.Split(" · ")[0];
        }

        private async Task ReviewBatchAsync(IReadOnlyList<MemoryRecord> batch, CancellationToken cancellationToken)
        {
            var listing = new StringBuilder();
            foreach (var memory in batch)
            {
                listing.AppendLine($"[{memory.Id}] {memory.Type}: {memory.Content} (saved {memory.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, used {memory.AccessCount}×)");
            }

            var requestMessages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject { ["role"] = "user", ["content"] = "Memories to review:\n" + listing },
            };
            var responseMessage = await lmStudioClient.StreamCompletionAsync(requestMessages, null, ThinkingLevel.Light, (_, _) => Task.FromResult(true), cancellationToken);
            var (_, answer) = ThinkingSplitter.Split(GetString(responseMessage["content"]), GetString(responseMessage["reasoning_content"]));
            var batchIds = batch.Select(memory => memory.Id).ToHashSet();
            var saved = 0;
            foreach (var proposalNode in ReadProposalNodes(answer).Take(MaximumProposalsPerBatch))
            {
                var kind = GetString(proposalNode["kind"])?.ToLowerInvariant() ?? "";
                var memoryIds = (proposalNode["ids"] as JsonArray ?? [])
                    .Select(idNode => GetString(idNode))
                    .OfType<string>()
                    .Where(batchIds.Contains)
                    .Distinct()
                    .ToList();
                var newContent = GetString(proposalNode["content"])?.Trim();
                var newType = GetString(proposalNode["type"])?.Trim().ToLowerInvariant();
                var reason = GetString(proposalNode["reason"])?.Trim() ?? "";
                var isValid = Kinds.Contains(kind) && memoryIds.Count > 0 && kind switch
                {
                    "merge" => memoryIds.Count >= 2 && newContent is { Length: >= 12 and <= 500 } && newType is not null && MemoryTypes.All.Contains(newType) && newType != MemoryTypes.Archive,
                    "retype" => memoryIds.Count == 1 && newType is not null && MemoryTypes.All.Contains(newType) && newType != MemoryTypes.Archive,
                    _ => true,
                };
                if (!isValid)
                {
                    continue;
                }

                AddProposal(kind, memoryIds, kind == "merge" ? newContent : null, kind is "merge" or "retype" ? newType : null, reason);
                saved++;
            }

            logger.LogInformation("Memory review: {Saved} proposals from a batch of {Count}", saved, batch.Count);
        }

        /// <summary>
        /// Applies or rejects a proposal. One whose memories changed since (deleted, merged elsewhere) becomes stale instead.
        /// </summary>
        public string Decide(long proposalId, bool accept)
        {
            var proposal = ListProposals(null).FirstOrDefault(candidate => candidate.Id == proposalId);
            if (proposal is null || proposal.Status != "open")
            {
                return "gone";
            }

            if (!accept)
            {
                SetStatus(proposalId, "rejected");
                return "rejected";
            }

            var currentMemories = memoryStore.GetByIds(proposal.MemoryIds);
            if (currentMemories.Count != proposal.MemoryIds.Count || currentMemories.Any(memory => memory.Type == MemoryTypes.Archive))
            {
                SetStatus(proposalId, "stale");
                return "stale";
            }

            switch (proposal.Kind)
            {
                case "merge":
                    var mergedTags = currentMemories.SelectMany(memory => memory.Tags).Distinct().ToList();
                    memoryStore.Supersede(proposal.MemoryIds, proposal.NewContent!, proposal.NewType!, mergedTags, "review");
                    break;
                case "archive":
                    foreach (var memoryId in proposal.MemoryIds)
                    {
                        memoryStore.SetType(memoryId, MemoryTypes.Archive);
                    }

                    break;
                case "retype":
                    memoryStore.SetType(proposal.MemoryIds[0], proposal.NewType!);
                    break;
                case "delete":
                    foreach (var memoryId in proposal.MemoryIds)
                    {
                        memoryStore.Delete(memoryId);
                    }

                    break;
            }

            SetStatus(proposalId, "accepted");
            return "accepted";
        }

        public IReadOnlyList<MemoryProposalView> ListOpenWithMemories()
        {
            return ListProposals("open")
                .Select(proposal => new MemoryProposalView(proposal, memoryStore.GetByIds(proposal.MemoryIds)))
                .ToList();
        }

        private IReadOnlyList<MemoryProposal> ListProposals(string? status)
        {
            lock (databaseLock)
            {
                using var command = CreateCommand(status is null ? "SELECT id, kind, memory_ids, new_content, new_type, reason, status, created_at FROM memory_proposals ORDER BY id;" : "SELECT id, kind, memory_ids, new_content, new_type, reason, status, created_at FROM memory_proposals WHERE status = $status ORDER BY id;",
                    ("$status", status));
                using var reader = command.ExecuteReader();
                var proposals = new List<MemoryProposal>();
                while (reader.Read())
                {
                    proposals.Add(new MemoryProposal(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [],
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).ToLocalTime()));
                }

                return proposals;
            }
        }

        private void AddProposal(string kind, IReadOnlyList<string> memoryIds, string? newContent, string? newType, string reason)
        {
            lock (databaseLock)
            {
                Execute("INSERT INTO memory_proposals (kind, memory_ids, new_content, new_type, reason, created_at) VALUES ($kind, $ids, $content, $type, $reason, $now);",
                    ("$kind", kind),
                    ("$ids", JsonSerializer.Serialize(memoryIds)),
                    ("$content", newContent),
                    ("$type", newType),
                    ("$reason", reason),
                    ("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds()));
            }
        }

        private void SetStatus(long proposalId, string status)
        {
            lock (databaseLock)
            {
                Execute("UPDATE memory_proposals SET status = $status, decided_at = $now WHERE id = $id;",
                    ("$status", status),
                    ("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds()),
                    ("$id", proposalId));
            }
        }

        private static IEnumerable<JsonNode> ReadProposalNodes(string answer)
        {
            var startIndex = answer.IndexOf('{');
            var endIndex = answer.LastIndexOf('}');
            if (startIndex < 0 || endIndex <= startIndex)
            {
                return [];
            }

            try
            {
                return (JsonNode.Parse(answer[startIndex..(endIndex + 1)])?["proposals"] as JsonArray ?? [])
                    .OfType<JsonNode>()
                    .ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static string? GetString(JsonNode? node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        private SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return command;
        }

        private int Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }
}