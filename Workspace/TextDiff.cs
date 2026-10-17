using System.Text;

namespace Rhodes.Core.Workspace;

/// <summary>
/// A short before/after view of a change for the approval card: the lines that differ, with a little context.
/// It trims the unchanged start and end, so a change in the middle of a big file shows only the middle.
/// </summary>
public static class TextDiff
{
    private const int ContextLines = 2;
    private const int MaximumShownLines = 80;

    public static string Describe(string? oldText, string newText)
    {
        var newLines = SplitLines(newText);
        if (oldText is null)
        {
            var preview = newLines.Take(MaximumShownLines).Select((line, index) => $"+{index + 1,5}  {line}");
            var more = newLines.Length > MaximumShownLines ? $"\n  … {newLines.Length - MaximumShownLines} more lines" : "";
            return $"New file, {newLines.Length} lines:\n" + string.Join('\n', preview) + more;
        }

        var oldLines = SplitLines(oldText);
        var commonStart = 0;
        while (commonStart < oldLines.Length && commonStart < newLines.Length && oldLines[commonStart] == newLines[commonStart])
        {
            commonStart++;
        }

        var commonEnd = 0;
        while (commonEnd < oldLines.Length - commonStart && commonEnd < newLines.Length - commonStart && oldLines[^(commonEnd + 1)] == newLines[^(commonEnd + 1)])
        {
            commonEnd++;
        }

        if (commonStart == oldLines.Length && commonStart == newLines.Length)
        {
            return "No change: the new content is the same as the file.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{oldLines.Length} → {newLines.Length} lines, changes around line {commonStart + 1}:");
        var shownLines = 0;
        void AddLine(string marker, int lineNumber, string line)
        {
            if (shownLines == MaximumShownLines)
            {
                builder.AppendLine("  … (more changes not shown)");
            }

            if (shownLines++ < MaximumShownLines)
            {
                builder.AppendLine($"{marker}{lineNumber,5}  {line}");
            }
        }

        for (var index = Math.Max(0, commonStart - ContextLines); index < commonStart; index++)
        {
            AddLine(" ", index + 1, oldLines[index]);
        }

        for (var index = commonStart; index < oldLines.Length - commonEnd; index++)
        {
            AddLine("-", index + 1, oldLines[index]);
        }

        for (var index = commonStart; index < newLines.Length - commonEnd; index++)
        {
            AddLine("+", index + 1, newLines[index]);
        }

        for (var index = newLines.Length - commonEnd; index < Math.Min(newLines.Length, newLines.Length - commonEnd + ContextLines); index++)
        {
            AddLine(" ", index + 1, newLines[index]);
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n');
    }
}