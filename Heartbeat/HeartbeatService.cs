using Rhodes.Core.Chat;

namespace Rhodes.Core.Heartbeat;

/// <summary>
/// What the dashboard shows about the heartbeat. WaitingFor says why she isn't waking right now; null means she's due.
/// </summary>
public sealed record HeartbeatStatus(bool Enabled, bool Running, string? WaitingFor, DateTimeOffset? LastTickAt, DateTimeOffset LastActivityAt, int IdleMinutes, int IntervalMinutes, bool AllowWeb);

public enum ManualTickOutcome
{
    Ran,
    Busy,
    ModelUnavailable,
}

/// <summary>
/// Wakes Closure on her own, but only when nobody needs the model: the core checks once a minute and runs a tick when
/// Shapto has been quiet for IdleMinutes, the last tick was IntervalMinutes ago, and the laptop has been awake for WakeGraceMinutes.
/// Sleep and wake are noticed by the gap between checks; missed ticks are never made up, so waking from sleep never causes a burst.
/// A chat that starts during a tick stops it.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SleepGap = TimeSpan.FromMinutes(5);

    private readonly HeartbeatRunner heartbeatRunner;
    private readonly HeartbeatStore heartbeatStore;
    private readonly ActivityTracker activityTracker;
    private readonly ModelStatusMonitor modelStatusMonitor;
    private readonly ILogger<HeartbeatService> logger;
    private readonly bool enabled;
    private readonly TimeSpan idleTime;
    private readonly TimeSpan tickInterval;
    private readonly TimeSpan wakeGrace;
    private readonly SemaphoreSlim tickLock = new(1, 1);
    private readonly object cancellationLock = new();

    private DateTimeOffset lastCheckAt = DateTimeOffset.Now;
    private DateTimeOffset awakeSince = DateTimeOffset.Now;
    private bool awakeSinceIsWake;
    private CancellationTokenSource? runningTickCancellation;

    public HeartbeatService(HeartbeatRunner heartbeatRunner, HeartbeatStore heartbeatStore, ActivityTracker activityTracker, ModelStatusMonitor modelStatusMonitor, IConfiguration configuration, ILogger<HeartbeatService> logger)
    {
        this.heartbeatRunner = heartbeatRunner;
        this.heartbeatStore = heartbeatStore;
        this.activityTracker = activityTracker;
        this.modelStatusMonitor = modelStatusMonitor;
        this.logger = logger;
        enabled = configuration.GetValue("Heartbeat:Enabled", true);
        idleTime = TimeSpan.FromMinutes(configuration.GetValue("Heartbeat:IdleMinutes", 45));
        tickInterval = TimeSpan.FromMinutes(configuration.GetValue("Heartbeat:IntervalMinutes", 120));
        wakeGrace = TimeSpan.FromMinutes(configuration.GetValue("Heartbeat:WakeGraceMinutes", 20));
        activityTracker.ChatStarted += StopRunningTick;
    }

    public HeartbeatStatus GetStatus()
    {
        var now = DateTimeOffset.Now;
        return new HeartbeatStatus(enabled, tickLock.CurrentCount == 0, DescribeWait(now), heartbeatStore.GetLastTickAt(), activityTracker.LastActivityAt, (int)idleTime.TotalMinutes, (int)tickInterval.TotalMinutes, heartbeatRunner.AllowWeb);
    }

    /// <summary>
    /// The dashboard's "run a tick now": skips the idle and interval waits, but never runs over a chat or another tick.
    /// </summary>
    public async Task<(ManualTickOutcome Outcome, HeartbeatTick? Tick)> RunNowAsync(CancellationToken cancellationToken)
    {
        if (modelStatusMonitor.Current.State != ModelStates.Online)
        {
            return (ManualTickOutcome.ModelUnavailable, null);
        }

        if (activityTracker.IsChatRunning || !await tickLock.WaitAsync(0, cancellationToken))
        {
            return (ManualTickOutcome.Busy, null);
        }

        try
        {
            return (ManualTickOutcome.Ran, await RunTickWithLockAsync("manual", cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return (ManualTickOutcome.Busy, null);
        }
        finally
        {
            tickLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        {
            logger.LogInformation("Heartbeat is turned off (Heartbeat:Enabled)");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.Now;
            if (now - lastCheckAt > SleepGap)
            {
                // The timer couldn't fire while the laptop slept, so a long gap between checks means it just woke up.
                awakeSince = now;
                awakeSinceIsWake = true;
                logger.LogInformation("Heartbeat: the laptop woke up after {Minutes:0} min; waiting {Grace} min before ticking", (now - lastCheckAt).TotalMinutes, wakeGrace.TotalMinutes);
            }

            lastCheckAt = now;
            if (DescribeWait(now) is not null || !await tickLock.WaitAsync(0, stoppingToken))
            {
                continue;
            }

            try
            {
                await RunTickWithLockAsync("idle", stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // A chat started; the tick already recorded itself as interrupted.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Heartbeat tick crashed");
            }
            finally
            {
                tickLock.Release();
            }
        }
    }

    /// <summary>
    /// Runs one tick (the caller holds tickLock), then warms the latest chat's cache back up.
    /// A chat starting at any point cancels both.
    /// </summary>
    private async Task<HeartbeatTick> RunTickWithLockAsync(string trigger, CancellationToken cancellationToken)
    {
        using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (cancellationLock)
        {
            runningTickCancellation = tickCancellation;
        }

        try
        {
            var tick = await heartbeatRunner.RunTickAsync(trigger, tickCancellation.Token);
            try
            {
                await heartbeatRunner.WarmUpLatestChatAsync(tickCancellation.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Heartbeat: warming the latest chat back up failed");
            }

            return tick;
        }
        finally
        {
            lock (cancellationLock)
            {
                runningTickCancellation = null;
            }
        }
    }

    private void StopRunningTick()
    {
        lock (cancellationLock)
        {
            runningTickCancellation?.Cancel();
        }
    }

    /// <summary>
    /// Why she isn't due right now, in words for the dashboard; null when every condition is met.
    /// </summary>
    private string? DescribeWait(DateTimeOffset now)
    {
        if (!enabled)
        {
            return "turned off in settings";
        }

        if (tickLock.CurrentCount == 0)
        {
            return "a tick is running";
        }

        if (modelStatusMonitor.Current.State != ModelStates.Online)
        {
            return "the model isn't online";
        }

        if (activityTracker.IsChatRunning)
        {
            return "a chat is running";
        }

        var quietFor = now - activityTracker.LastActivityAt;
        if (quietFor < idleTime)
        {
            return $"you were active {FormatMinutes(quietFor)} ago · waits for {FormatMinutes(idleTime)} of quiet";
        }

        var awakeFor = now - awakeSince;
        if (awakeFor < wakeGrace)
        {
            return $"{(awakeSinceIsWake ? "the laptop woke up" : "the core started")} {FormatMinutes(awakeFor)} ago · waits {FormatMinutes(wakeGrace)}";
        }

        var lastTickAt = heartbeatStore.GetLastTickAt();
        if (lastTickAt is not null && now - lastTickAt.Value < tickInterval)
        {
            return $"last tick {FormatMinutes(now - lastTickAt.Value)} ago · one every {FormatMinutes(tickInterval)}";
        }

        return null;
    }

    private static string FormatMinutes(TimeSpan duration)
    {
        return duration.TotalMinutes < 120 ? $"{Math.Max(0, (int)duration.TotalMinutes)} min" : $"{duration.TotalHours:0.#} h";
    }

    public override void Dispose()
    {
        activityTracker.ChatStarted -= StopRunningTick;
        tickLock.Dispose();
        base.Dispose();
    }
}