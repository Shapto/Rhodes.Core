using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Rhodes.Core.Documents;

namespace Rhodes.Core.Knowledge;

/// <summary>
/// Splits a knowledge file's text into chunks by its kind: documents by headings, RimWorld Def files by their top-level Defs,
/// everything else (code, configs, data, logs) by overlapping line windows. Every chunk keeps its exact line range so read_knowledge can show it.
/// </summary>
public static partial class KnowledgeChunker
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc", ".org", ".html", ".htm", ".xhtml", ".docx", ".pdf", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
    };

    public const int MaximumDocumentChunkLines = 120;
    public const int CodeWindowLines = 60;
    public const int CodeWindowOverlap = 10;

    /// <summary>
    /// The chunks of one file. fileName decides the kind; text is the whole file as TextExtractor read it.
    /// </summary>
    public static IReadOnlyList<KnowledgeChunk> Split(string fileName, string text)
    {
        var lines = SplitLines(text);
        if (lines.Length == 0 || lines.All(string.IsNullOrWhiteSpace))
        {
            return [];
        }

        if (TextExtractor.IsArchive(fileName))
        {
            return SplitArchive(fileName, lines);
        }

        var extension = Path.GetExtension(fileName);
        if (DocumentExtensions.Contains(extension))
        {
            return SplitByHeadings(fileName, lines);
        }

        if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return SplitXmlDefs(fileName, text, lines) ?? SplitIntoWindows(Path.GetFileName(fileName), lines);
        }

        return SplitIntoWindows(BuildCodeTitle(fileName, lines), lines);
    }

    public static string[] SplitLines(string text)
    {
        var normalizedText = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalizedText.EndsWith('\n'))
        {
            normalizedText = normalizedText[..^1];
        }

        return normalizedText.Length == 0 ? [] : normalizedText.Split('\n');
    }

    /// <summary>
    /// An archive's text (see TextExtractor): the summary at the top is one chunk, and every file inside is chunked
    /// as its own kind (code in windows, docs by headings, Defs per Def), with line numbers counted in the whole archive text.
    /// </summary>
    private static List<KnowledgeChunk> SplitArchive(string archiveName, string[] lines)
    {
        var markerIndexes = new List<int>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lines[lineIndex].StartsWith(TextExtractor.ArchiveEntryMarkerStart, StringComparison.Ordinal) && lines[lineIndex].EndsWith(TextExtractor.ArchiveEntryMarkerEnd, StringComparison.Ordinal))
            {
                markerIndexes.Add(lineIndex);
            }
        }

        var chunks = new List<KnowledgeChunk>();
        var summaryLastIndex = markerIndexes.Count > 0 ? markerIndexes[0] - 1 : lines.Length - 1;
        if (summaryLastIndex >= 0)
        {
            AddSection(chunks, $"{Path.GetFileName(archiveName)} — contents", lines, 0, summaryLastIndex);
        }

        for (var markerNumber = 0; markerNumber < markerIndexes.Count; markerNumber++)
        {
            var markerLine = lines[markerIndexes[markerNumber]];
            var entryPath = markerLine[TextExtractor.ArchiveEntryMarkerStart.Length..^TextExtractor.ArchiveEntryMarkerEnd.Length];
            var firstIndex = markerIndexes[markerNumber] + 1;
            var lastIndex = markerNumber + 1 < markerIndexes.Count ? markerIndexes[markerNumber + 1] - 1 : lines.Length - 1;
            if (lastIndex < firstIndex)
            {
                continue;
            }

            var entryText = string.Join('\n', lines[firstIndex..(lastIndex + 1)]);
            var entryFileName = Path.GetFileName(entryPath);
            foreach (var entryChunk in Split(entryPath, entryText))
            {
                // Titles that start with the file's name ("Thing.cs — class Thing") get the full path in its place instead of twice.
                var title = entryChunk.Title.StartsWith(entryFileName, StringComparison.Ordinal) ? entryPath + entryChunk.Title[entryFileName.Length..] : $"{entryPath} — {entryChunk.Title}";
                chunks.Add(new KnowledgeChunk(title, entryChunk.Content, entryChunk.StartLine + firstIndex, entryChunk.EndLine + firstIndex));
            }
        }

        return chunks;
    }

    /// <summary>
    /// One chunk per heading section (text before the first heading counts as a section titled with the file name).
    /// A section longer than MaximumDocumentChunkLines is cut into parts.
    /// </summary>
    private static List<KnowledgeChunk> SplitByHeadings(string fileName, string[] lines)
    {
        var chunks = new List<KnowledgeChunk>();
        var sectionTitle = Path.GetFileName(fileName);
        var sectionStartIndex = 0;

        for (var lineIndex = 0; lineIndex <= lines.Length; lineIndex++)
        {
            var isEnd = lineIndex == lines.Length;
            var headingMatch = isEnd ? null : MarkdownHeadingPattern().Match(lines[lineIndex]);
            if (!isEnd && !headingMatch!.Success)
            {
                continue;
            }

            if (lineIndex > sectionStartIndex)
            {
                AddSection(chunks, sectionTitle, lines, sectionStartIndex, lineIndex - 1);
            }

            if (!isEnd)
            {
                sectionTitle = headingMatch!.Groups["title"].Value.Trim();
                sectionStartIndex = lineIndex;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Adds the lines firstIndex..lastIndex (0-based, inclusive) as one or more chunks, skipping blank-only sections.
    /// </summary>
    private static void AddSection(List<KnowledgeChunk> chunks, string title, string[] lines, int firstIndex, int lastIndex)
    {
        var sectionLines = lines[firstIndex..(lastIndex + 1)];
        if (sectionLines.All(string.IsNullOrWhiteSpace))
        {
            return;
        }

        var partCount = (sectionLines.Length + MaximumDocumentChunkLines - 1) / MaximumDocumentChunkLines;
        for (var partIndex = 0; partIndex < partCount; partIndex++)
        {
            var partFirstIndex = firstIndex + partIndex * MaximumDocumentChunkLines;
            var partLastIndex = Math.Min(lastIndex, partFirstIndex + MaximumDocumentChunkLines - 1);
            var partTitle = partCount > 1 ? $"{title} (part {partIndex + 1} of {partCount})" : title;
            chunks.Add(new KnowledgeChunk(partTitle, string.Join('\n', lines[partFirstIndex..(partLastIndex + 1)]), partFirstIndex + 1, partLastIndex + 1));
        }
    }

    /// <summary>
    /// Windows of CodeWindowLines lines that overlap by CodeWindowOverlap, so code that spans a window edge is still found whole.
    /// </summary>
    private static List<KnowledgeChunk> SplitIntoWindows(string title, string[] lines)
    {
        var chunks = new List<KnowledgeChunk>();
        var windowStep = CodeWindowLines - CodeWindowOverlap;
        for (var windowFirstIndex = 0; windowFirstIndex < lines.Length; windowFirstIndex += windowStep)
        {
            var windowLastIndex = Math.Min(lines.Length - 1, windowFirstIndex + CodeWindowLines - 1);
            chunks.Add(new KnowledgeChunk(title, string.Join('\n', lines[windowFirstIndex..(windowLastIndex + 1)]), windowFirstIndex + 1, windowLastIndex + 1));
            if (windowLastIndex == lines.Length - 1)
            {
                break;
            }
        }

        return chunks;
    }

    /// <summary>
    /// "Thing.cs — class Thing": the file name plus the type it declares, so a search for the type name ranks its file first.
    /// The type named like the file wins; otherwise the first one declared.
    /// </summary>
    private static string BuildCodeTitle(string fileName, string[] lines)
    {
        var typeMatches = lines
            .Select(line => TypeDeclarationPattern().Match(line))
            .Where(match => match.Success)
            .ToList();
        var fileTypeName = Path.GetFileNameWithoutExtension(fileName);
        var typeMatch = typeMatches.FirstOrDefault(match => match.Groups["name"].Value == fileTypeName) ?? typeMatches.FirstOrDefault();
        return typeMatch is null ? Path.GetFileName(fileName) : $"{Path.GetFileName(fileName)} — {typeMatch.Groups["kind"].Value} {typeMatch.Groups["name"].Value}";
    }

    /// <summary>
    /// One chunk per top-level element whose name ends in "Def" (ThingDef, HediffDef, …), titled "ThingDef MeleeWeapon_Knife".
    /// Returns null when the file isn't valid XML or has no Defs, so the caller falls back to line windows.
    /// </summary>
    private static List<KnowledgeChunk>? SplitXmlDefs(string fileName, string text, string[] lines)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }

        var topElements = document.Root?.Elements().ToList() ?? [];
        var defElements = topElements
            .Where(element => element.Name.LocalName.EndsWith("Def", StringComparison.Ordinal))
            .ToList();
        if (defElements.Count == 0)
        {
            return null;
        }

        var chunks = new List<KnowledgeChunk>();
        foreach (var element in defElements)
        {
            // A Def ends where the next node starts (another element, or a comment that belongs to what follows).
            var startLine = ((IXmlLineInfo)element).LineNumber;
            var nextStartLine = element.NextNode is IXmlLineInfo nextNodeInfo && nextNodeInfo.HasLineInfo() ? nextNodeInfo.LineNumber : FindRootClosingLine(lines);
            var endLine = Math.Max(startLine, nextStartLine - 1);
            while (endLine > startLine && string.IsNullOrWhiteSpace(lines[endLine - 1]))
            {
                endLine--;
            }

            var defName = element.Element("defName")?.Value.Trim() ?? element.Attribute("Name")?.Value.Trim() ?? "";
            var title = defName.Length > 0 ? $"{element.Name.LocalName} {defName}" : element.Name.LocalName;
            chunks.Add(new KnowledgeChunk(title, string.Join('\n', lines[(startLine - 1)..endLine]), startLine, endLine));
        }

        return chunks;
    }

    /// <summary>
    /// The 1-based line of the root's closing tag (the last non-blank line), so the last Def doesn't swallow it.
    /// </summary>
    private static int FindRootClosingLine(string[] lines)
    {
        for (var lineIndex = lines.Length - 1; lineIndex >= 0; lineIndex--)
        {
            if (!string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                return lineIndex + 1;
            }
        }

        return lines.Length;
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+)$")]
    private static partial Regex MarkdownHeadingPattern();

    [GeneratedRegex(@"\b(?<kind>class|struct|interface|enum|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclarationPattern();
}
