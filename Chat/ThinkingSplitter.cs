namespace Rhodes.Core.Chat;

/// <summary>
/// Separates thinking from the answer and removes stray think tags, so a leak
/// never reaches stored history and can't snowball into later turns.
/// </summary>
public static class ThinkingSplitter
{
    private const string OpeningTag = "<think>";
    private const string ClosingTag = "</think>";

    public static (string Reasoning, string Answer) Split(string? content, string? separateReasoning)
    {
        var remainingText = content ?? "";
        var reasoning = separateReasoning ?? "";

        var closingIndex = remainingText.IndexOf(ClosingTag, StringComparison.Ordinal);
        if (closingIndex >= 0)
        {
            reasoning = reasoning + "\n" + remainingText[..closingIndex];
            remainingText = remainingText[(closingIndex + ClosingTag.Length)..];

            var secondClosingIndex = remainingText.IndexOf(ClosingTag, StringComparison.Ordinal);
            if (secondClosingIndex >= 0)
            {
                remainingText = remainingText[..secondClosingIndex];
            }
        }

        var answer = remainingText.Replace(OpeningTag, "").Replace(ClosingTag, "").Trim();
        return (reasoning.Replace(OpeningTag, "").Trim(), answer);
    }
}
