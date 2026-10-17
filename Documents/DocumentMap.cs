using System.Text;
using System.Text.RegularExpressions;

namespace Rhodes.Core.Documents;

/// <summary>
/// A short overview of a big text, so Closure knows its shape before she searches it:
/// classes and methods with line numbers for code, defNames for RimWorld XML, and the most repeated errors for logs.
/// </summary>
public static partial class DocumentMap
{
    private const int MaximumEntries = 60;
    private const int MaximumMapCharacters = 2500;
    private const int MaximumRepeatedErrors = 6;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".java", ".js", ".ts", ".py", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".kt", ".lua" };

    public static string Describe(DocumentRecord document, string[] lines)
    {
        var builder = new StringBuilder();
        builder.Append($"Document {document.Id}: {document.Name} · {document.LineCount:N0} lines · about {(int)(document.Characters * DocumentStore.TokensPerCharacter):N0} tokens");
        builder.AppendLine();

        var extension = Path.GetExtension(document.Name);
        var looksLikeLog = extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || document.Name.Contains("log", StringComparison.OrdinalIgnoreCase);
        if (CodeExtensions.Contains(extension))
        {
            AppendEntries(builder, "Declarations", FindDeclarations(lines));
        }
        else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            AppendEntries(builder, "defNames", FindDefinitionNames(lines));
        }
        else if (looksLikeLog)
        {
            AppendLogSummary(builder, lines);
        }

        var map = builder.ToString();
        return map.Length > MaximumMapCharacters ? map[..MaximumMapCharacters] + "\n[map cut short]" : map;
    }

    private static void AppendEntries(StringBuilder builder, string heading, List<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        builder.AppendLine(heading + ":");
        foreach (var entry in entries.Take(MaximumEntries))
        {
            builder.AppendLine("  " + entry);
        }

        if (entries.Count > MaximumEntries)
        {
            builder.AppendLine($"  … and {entries.Count - MaximumEntries} more");
        }
    }

    private static List<string> FindDeclarations(string[] lines)
    {
        var entries = new List<string>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var typeMatch = TypeDeclarationPattern().Match(lines[lineIndex]);
            if (typeMatch.Success)
            {
                entries.Add($"line {lineIndex + 1}: {typeMatch.Groups["kind"].Value} {typeMatch.Groups["name"].Value}");
                continue;
            }

            var methodMatch = MethodDeclarationPattern().Match(lines[lineIndex]);
            if (methodMatch.Success)
            {
                entries.Add($"line {lineIndex + 1}:   {methodMatch.Groups["name"].Value}()");
            }
        }

        return entries;
    }

    private static List<string> FindDefinitionNames(string[] lines)
    {
        var entries = new List<string>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (Match definitionMatch in DefinitionNamePattern().Matches(lines[lineIndex]))
            {
                entries.Add($"line {lineIndex + 1}: {definitionMatch.Groups["name"].Value}");
            }
        }

        return entries;
    }

    /// <summary>
    /// Counts error and warning lines, and lists the errors that repeat most (numbers and hex addresses ignored, so "at 0x1F" and "at 0x2A" count as one).
    /// </summary>
    private static void AppendLogSummary(StringBuilder builder, string[] lines)
    {
        var errorCount = 0;
        var warningCount = 0;
        var repeatedErrors = new Dictionary<string, (int Count, int FirstLine, string Example)>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var isError = ErrorPattern().IsMatch(line);
            if (isError)
            {
                errorCount++;
                var normalizedLine = DigitPattern().Replace(line.Trim(), "#");
                if (normalizedLine.Length > 200)
                {
                    normalizedLine = normalizedLine[..200];
                }

                repeatedErrors[normalizedLine] = repeatedErrors.TryGetValue(normalizedLine, out var existing) ? (existing.Count + 1, existing.FirstLine, existing.Example) : (1, lineIndex + 1, line.Trim());
            }
            else if (WarningPattern().IsMatch(line))
            {
                warningCount++;
            }
        }

        builder.AppendLine($"Error lines: {errorCount:N0} · warning lines: {warningCount:N0}");
        var mostRepeated = repeatedErrors.Values
            .OrderByDescending(errorEntry => errorEntry.Count)
            .ThenBy(errorEntry => errorEntry.FirstLine)
            .Take(MaximumRepeatedErrors)
            .ToList();
        if (mostRepeated.Count == 0)
        {
            return;
        }

        builder.AppendLine("Most repeated errors:");
        foreach (var (count, firstLine, example) in mostRepeated)
        {
            var shownExample = example.Length > 180 ? example[..180] + "…" : example;
            builder.AppendLine($"  {count}× first at line {firstLine}: {shownExample}");
        }
    }

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|file|export|default)\s+)*(?<kind>class|interface|struct|enum|record|def|function)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(@"^\s*(?:public|private|protected|internal)\s+(?:(?:static|async|override|virtual|abstract|sealed|new|extern|unsafe|partial)\s+)*[\w<>\[\],.?\s]+?\s(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex MethodDeclarationPattern();

    [GeneratedRegex(@"<defName>(?<name>[^<]{1,120})</defName>")]
    private static partial Regex DefinitionNamePattern();

    [GeneratedRegex(@"exception|\berror\b|\bfailed to\b|\bcould not\b|\bfatal\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\bwarn(?:ing)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningPattern();

    [GeneratedRegex(@"0x[0-9A-Fa-f]+|\d+")]
    private static partial Regex DigitPattern();
}