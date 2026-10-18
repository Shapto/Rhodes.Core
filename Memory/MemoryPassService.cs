using Rhodes.Core.Chat;
using Rhodes.Core.Heartbeat;

namespace Rhodes.Core.Memory
{
    /// <summary>
    /// The background memory pass: once Shapto has been quiet for a few minutes, the newest messages of recent chats go
    /// through the MemoryExtractor, one chat per check, and the chat is marked as passed up to there.
    /// Afterwards the latest chat's cache is warmed back up, since the pass used llama-server's single slot.
    /// </summary>
    public sealed class MemoryPassService : BackgroundService
    {
        private const int RecentChatCount = 10;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RecentChatAge = TimeSpan.FromDays(7);

        private readonly MemoryExtractor memoryExtractor;
        private readonly ConversationStore conversationStore;
        private readonly ConversationManager conversationManager;
        private readonly ActivityTracker activityTracker;
        private readonly ModelStatusMonitor modelStatusMonitor;
        private readonly HeartbeatService heartbeatService;
        private readonly HeartbeatRunner heartbeatRunner;
        private readonly ILogger<MemoryPassService> logger;
        private readonly TimeSpan quietTime;
        private readonly bool enabled;

        public MemoryPassService(MemoryExtractor memoryExtractor, ConversationStore conversationStore, ConversationManager conversationManager, ActivityTracker activityTracker, ModelStatusMonitor modelStatusMonitor, HeartbeatService heartbeatService, HeartbeatRunner heartbeatRunner, IConfiguration configuration, ILogger<MemoryPassService> logger)
        {
            this.memoryExtractor = memoryExtractor;
            this.conversationStore = conversationStore;
            this.conversationManager = conversationManager;
            this.activityTracker = activityTracker;
            this.modelStatusMonitor = modelStatusMonitor;
            this.heartbeatService = heartbeatService;
            this.heartbeatRunner = heartbeatRunner;
            this.logger = logger;
            quietTime = TimeSpan.FromMinutes(configuration.GetValue("Memory:PassAfterQuietMinutes", 5));
            enabled = configuration.GetValue("Memory:BackgroundPass", true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!enabled)
            {
                return;
            }

            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnePassAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // A chat started; the chat isn't marked as passed, so it's picked up again next time.
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Background memory pass failed");
                }
            }
        }

        private async Task RunOnePassAsync(CancellationToken stoppingToken)
        {
            var isQuiet = DateTimeOffset.Now - activityTracker.LastActivityAt >= quietTime && !activityTracker.IsChatRunning;
            if (!isQuiet || heartbeatService.GetStatus().Running || modelStatusMonitor.Current.State != ModelStates.Online)
            {
                return;
            }

            var conversation = conversationStore.List(RecentChatCount)
                .Where(chatSummary => DateTimeOffset.UtcNow - chatSummary.UpdatedAt < RecentChatAge)
                .Select(chatSummary => conversationManager.Get(chatSummary.Id))
                .FirstOrDefault(candidate => candidate is not null && candidate.MemoryPassThrough < candidate.Messages.Count);
            if (conversation is null || !await conversation.TurnLock.WaitAsync(0, stoppingToken))
            {
                return;
            }

            // A chat starting stops the pass, so it never holds up a reply.
            using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            void StopPass() => passCancellation.Cancel();
            activityTracker.ChatStarted += StopPass;
            try
            {
                try
                {
                    var passEnd = conversation.Messages.Count;
                    await memoryExtractor.ExtractAsync(conversation, conversation.MemoryPassThrough, passEnd, passCancellation.Token);
                    conversation.MemoryPassThrough = passEnd;
                    conversationStore.SaveMemoryPassThrough(conversation);
                }
                finally
                {
                    conversation.TurnLock.Release();
                }

                // After the chat's lock is released: the warm-up takes the latest chat's lock itself.
                await heartbeatRunner.WarmUpLatestChatAsync(passCancellation.Token);
            }
            finally
            {
                activityTracker.ChatStarted -= StopPass;
            }
        }
    }
}