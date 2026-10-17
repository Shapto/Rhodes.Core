namespace Rhodes.Core.Heartbeat;

/// <summary>
/// Knows when Shapto last talked to Closure and whether a chat is running right now,
/// so the heartbeat only wakes her when the model is free and Shapto has been away for a while.
/// </summary>
public sealed class ActivityTracker
{
    private int runningChatCount;

    /// <summary>
    /// Starts as the moment the core started, so a fresh start (or the laptop booting) counts as activity.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.Now;

    public bool IsChatRunning => Volatile.Read(ref runningChatCount) > 0;

    /// <summary>
    /// Raised when a chat request starts, so a heartbeat tick that is using the model can stop and make room.
    /// </summary>
    public event Action? ChatStarted;

    /// <summary>
    /// Call at the start of a chat request and dispose at its end: both moments count as activity.
    /// </summary>
    public IDisposable BeginChat()
    {
        Interlocked.Increment(ref runningChatCount);
        LastActivityAt = DateTimeOffset.Now;
        ChatStarted?.Invoke();
        return new ChatScope(this);
    }

    private void EndChat()
    {
        Interlocked.Decrement(ref runningChatCount);
        LastActivityAt = DateTimeOffset.Now;
    }

    private sealed class ChatScope(ActivityTracker activityTracker) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                activityTracker.EndChat();
            }
        }
    }
}