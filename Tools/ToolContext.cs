namespace Rhodes.Core.Tools
{
    /// <summary>
    /// What a tool may need to know about the turn it runs in. Set by the chat loop at the start of each turn;
    /// it flows into every tool call of that turn and is null outside chats (heartbeat ticks, the workbench).
    /// </summary>
    public static class ToolContext
    {
        private static readonly AsyncLocal<string?> currentConversationId = new();

        private static readonly AsyncLocal<Func<Chat.ChatEvent, Task>?> emitEvent = new();

        public static string? CurrentConversationId
        {
            get => currentConversationId.Value;
            set => currentConversationId.Value = value;
        }

        /// <summary>
        /// Sends an event to the open chat page (approval cards, Little Handy's steps). Null when no page is listening.
        /// </summary>
        public static Func<Chat.ChatEvent, Task>? EmitEvent
        {
            get => emitEvent.Value;
            set => emitEvent.Value = value;
        }
    }
}