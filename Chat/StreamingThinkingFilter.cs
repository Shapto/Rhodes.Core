using System.Text;

namespace Rhodes.Core.Chat;

/// <summary>
/// Watches streamed content for think tags. Text before a lone closing tag is reclassified as thinking
/// (a "moveToReasoning" event); a second closing tag means a repetition loop, so it asks to stop.
/// </summary>
public sealed class StreamingThinkingFilter
{
    private const string OpeningTag = "<think>";
    private const string ClosingTag = "</think>";

    private readonly StringBuilder pendingText = new();
    private int closingTagsSeen;

    public bool StopRequested { get; private set; }

    public bool ThinkingEnded => closingTagsSeen > 0;

    public IReadOnlyList<ChatEvent> Feed(string contentDelta)
    {
        var events = new List<ChatEvent>();
        if (StopRequested)
        {
            return events;
        }

        pendingText.Append(contentDelta);
        var text = pendingText.ToString();
        pendingText.Clear();

        var closingIndex = text.IndexOf(ClosingTag, StringComparison.Ordinal);
        while (closingIndex >= 0)
        {
            AddContent(events, text[..closingIndex]);
            text = text[(closingIndex + ClosingTag.Length)..];
            closingTagsSeen++;
            if (closingTagsSeen > 1)
            {
                StopRequested = true;
                return events;
            }

            events.Add(new ChatEvent("moveToReasoning"));
            closingIndex = text.IndexOf(ClosingTag, StringComparison.Ordinal);
        }

        var heldBackLength = GetPossibleTagStartLength(text);
        AddContent(events, text[..^heldBackLength]);
        pendingText.Append(text[^heldBackLength..]);
        return events;
    }

    /// <summary>
    /// Releases whatever was held back once the stream has ended.
    /// </summary>
    public IReadOnlyList<ChatEvent> Flush()
    {
        var events = new List<ChatEvent>();
        if (!StopRequested)
        {
            AddContent(events, pendingText.ToString());
        }

        pendingText.Clear();
        return events;
    }

    private static void AddContent(List<ChatEvent> events, string text)
    {
        var cleanedText = text.Replace(OpeningTag, "");
        if (cleanedText.Length > 0)
        {
            events.Add(new ChatEvent("content", cleanedText));
        }
    }

    private static int GetPossibleTagStartLength(string text)
    {
        for (var length = Math.Min(text.Length, ClosingTag.Length - 1); length > 0; length--)
        {
            var tail = text[^length..];
            if (ClosingTag.StartsWith(tail, StringComparison.Ordinal) || OpeningTag.StartsWith(tail, StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }
}