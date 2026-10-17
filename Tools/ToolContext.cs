namespace Rhodes.Core.Tools
{
    /// <summary>
    /// What a tool may need to know about the turn it runs in. Set by the chat loop at the start of each turn;
    /// it flows into every tool call of that turn and is null outside chats (heartbeat ticks, the workbench).
    /// </summary>
    public static class ToolContext
    {
        private static readonly AsyncLocal<string?> currentConversationId = new();

        public static string? CurrentConversationId
        {
            get => currentConversationId.Value;
            set => currentConversationId.Value = value;
        }
    }
}