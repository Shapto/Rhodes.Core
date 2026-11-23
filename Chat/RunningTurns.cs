using System.Collections.Concurrent;

namespace Rhodes.Core.Chat;

/// <summary>
/// The chat turns that are running right now, by chat id. A turn keeps going when its page closes or navigates away;
/// only the Stop button (POST /chats/{id}/stop) or the core shutting down cancels it.
/// </summary>
public sealed class RunningTurns
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> turnCancellations = new();

    /// <summary>
    /// Registers a turn for the chat. Dispose the result when the turn ends; its Token is what the turn should run on.
    /// </summary>
    public RunningTurn Begin(string conversationId, CancellationToken shutdownToken)
    {
        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        turnCancellations[conversationId] = turnCancellation;
        return new RunningTurn(this, conversationId, turnCancellation);
    }

    /// <summary>
    /// Stops the chat's running turn. Returns false when nothing was running.
    /// </summary>
    public bool Stop(string conversationId)
    {
        if (!turnCancellations.TryGetValue(conversationId, out var turnCancellation))
        {
            return false;
        }

        try
        {
            turnCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between the lookup and the cancel.
            return false;
        }

        return true;
    }

    public bool IsRunning(string conversationId)
    {
        return turnCancellations.ContainsKey(conversationId);
    }

    private void End(string conversationId, CancellationTokenSource turnCancellation)
    {
        // Only remove our own entry: a newer turn of the same chat may already have replaced it.
        turnCancellations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(conversationId, turnCancellation));
        turnCancellation.Dispose();
    }

    public sealed class RunningTurn : IDisposable
    {
        private readonly RunningTurns runningTurns;
        private readonly string conversationId;
        private readonly CancellationTokenSource turnCancellation;

        public RunningTurn(RunningTurns runningTurns, string conversationId, CancellationTokenSource turnCancellation)
        {
            this.runningTurns = runningTurns;
            this.conversationId = conversationId;
            this.turnCancellation = turnCancellation;
        }

        public CancellationToken Token => turnCancellation.Token;

        public void Dispose()
        {
            runningTurns.End(conversationId, turnCancellation);
        }
    }
}
