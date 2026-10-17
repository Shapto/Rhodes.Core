using System.Text.RegularExpressions;
using Rhodes.Core.Memory;

namespace Rhodes.Core.Chat;

public enum ThinkingLevel
{
    Off,
    Light,
    Full,
}

/// <summary>
/// Picks a thinking level in Auto mode with cheap checks, no model call.
/// A wrong guess costs a /think resend or some waiting, nothing worse.
/// </summary>
public static class ThinkingClassifier
{
    private const int LongMessageWordCount = 12;

    private static readonly HashSet<string> QuestionStarts =
    [
        "why", "how", "what", "which", "who", "when", "where", "should", "can", "could", "would",
        "is", "are", "does", "did", "will", "explain", "compare",
    ];

    private static readonly HashSet<string> ActionStarts =
    [
        "search", "look", "find", "read", "check", "fetch", "open", "browse", "google", "research", "investigate", "debug", "fix", "review",
    ];

    private static readonly Regex CodeLikePattern = new(
    @"`|[{};]|=>|\w+\.\w+\(|(?-i:\b[A-Z]\w+\.[A-Z]\w+)|[A-Za-z]:\\|\w+/\w+\.\w+|\bv?\d+\.\d+(\.\d+)?\b|exception|error|crash|stack ?trace",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ThinkingLevel Classify(string messageText)
    {
        var trimmedText = messageText.Trim();
        if (CodeLikePattern.IsMatch(trimmedText) || MemoryStore.GetSearchWords(trimmedText).Count > LongMessageWordCount)
        {
            return ThinkingLevel.Full;
        }

        var firstWord = trimmedText.Split(' ', 2)[0].ToLowerInvariant().TrimEnd(',', '.', '!');
        var isQuestion = trimmedText.Contains('?') || QuestionStarts.Contains(firstWord);
        return isQuestion || ActionStarts.Contains(firstWord) ? ThinkingLevel.Light : ThinkingLevel.Off;
    }
}
