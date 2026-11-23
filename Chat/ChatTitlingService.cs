using Rhodes.Core.Heartbeat;

namespace Rhodes.Core.Chat;

/// <summary>
/// Names new chats once Shapto has been quiet for a couple of minutes, instead of right after the first reply.
/// A title request takes llama-server's slot, so doing it mid-conversation wiped the chat's cache and made his next
/// message wait for a full re-read. Here it runs while nobody is waiting, one chat per check, and afterwards the
/// latest chat's cache is warmed back up. Until then the chat keeps the first-60-characters title ChatLoop gives it.
/// A chat that starts stops it; the chat stays pending and is titled at the next quiet spell.
/// </summary>
public sealed class ChatTitlingService : BackgroundService
{
    private const int RecentChatCount = 10;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly ChatTitler chatTitler;
    private readonly ConversationStore conversationStore;
    private readonly ConversationManager conversationManager;
    private readonly ActivityTracker activityTracker;
    private readonly ModelStatusMonitor modelStatusMonitor;
    private readonly HeartbeatService heartbeatService;
    private readonly HeartbeatRunner heartbeatRunner;
    private readonly ILogger<ChatTitlingService> logger;
    private readonly TimeSpan quietTime;

    private volatile bool isRunning;

    public ChatTitlingService(ChatTitler chatTitler, ConversationStore conversationStore, ConversationManager conversationManager, ActivityTracker activityTracker, ModelStatusMonitor modelStatusMonitor, HeartbeatService heartbeatService, HeartbeatRunner heartbeatRunner, IConfiguration configuration, ILogger<ChatTitlingService> logger)
    {
        this.chatTitler = chatTitler;
        this.conversationStore = conversationStore;
        this.conversationManager = conversationManager;
        this.activityTracker = activityTracker;
        this.modelStatusMonitor = modelStatusMonitor;
        this.heartbeatService = heartbeatService;
        this.heartbeatRunner = heartbeatRunner;
        this.logger = logger;
        quietTime = TimeSpan.FromMinutes(configuration.GetValue("Chat:TitleAfterQuietMinutes", 2));
    }

    /// <summary>
    /// True while a title request or its cache warm-up is running, so the memory pass can wait its turn.
    /// </summary>
    public bool IsRunning => isRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TitleOneChatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // A chat started; the chat stays pending and is picked up again next time.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Background chat titling failed");
            }
        }
    }

    private async Task TitleOneChatAsync(CancellationToken stoppingToken)
    {
        var isQuiet = DateTimeOffset.Now - activityTracker.LastActivityAt >= quietTime && !activityTracker.IsChatRunning;
        if (!isQuiet || heartbeatService.GetStatus().Running || modelStatusMonitor.Current.State != ModelStates.Online)
        {
            return;
        }

        var conversation = conversationStore.List(RecentChatCount)
            .Select(chatSummary => conversationManager.Get(chatSummary.Id))
            .FirstOrDefault(candidate => candidate is not null && (ChatTitler.NeedsTitle(candidate) || (candidate.TitlePending && candidate.TitleIsManual)));
        if (conversation is null)
        {
            return;
        }

        // A chat starting stops the titling, so it never holds up a reply.
        using var titlingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        void StopTitling() => titlingCancellation.Cancel();
        activityTracker.ChatStarted += StopTitling;
        isRunning = true;
        try
        {
            var sentTitleRequest = await chatTitler.TitlePendingChatAsync(conversation, titlingCancellation.Token);
            if (!sentTitleRequest)
            {
                return;
            }

            // After TitlePendingChatAsync released the chat's lock: the warm-up takes the latest chat's lock itself.
            await heartbeatRunner.WarmUpLatestChatAsync(titlingCancellation.Token);
        }
        finally
        {
            isRunning = false;
            activityTracker.ChatStarted -= StopTitling;
        }
    }
}
