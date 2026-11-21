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
            return SplitXmlDefs(fileName, text, lines) ?? SplitDotNetApiDocs(text, lines) ?? SplitIntoWindows(Path.GetFileName(fileName), lines);
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
            var headingTitle = isEnd ? null : FindHeadingTitle(lines, lineIndex);
            if (!isEnd && headingTitle is null)
            {
                continue;
            }

            if (lineIndex > sectionStartIndex)
            {
                AddSection(chunks, sectionTitle, lines, sectionStartIndex, lineIndex - 1);
            }

            if (!isEnd)
            {
                sectionTitle = headingTitle!;
                sectionStartIndex = lineIndex;
            }
        }

        return chunks;
    }

    /// <summary>
    /// The title when a section starts at this line, otherwise null. Three kinds:
    /// a Markdown heading ("## Title"); a reStructuredText heading (a line underlined with =, -, ~ and the like, as in the Godot docs);
    /// and a Godot class-reference anchor (".. _class_Node_method_add_child:"), so every method, property and signal gets its own chunk
    /// titled with its signature ("Node.add_child (method) — void add_child(node: Node, …)").
    /// </summary>
    private static string? FindHeadingTitle(string[] lines, int lineIndex)
    {
        var line = lines[lineIndex];
        var markdownMatch = MarkdownHeadingPattern().Match(line);
        if (markdownMatch.Success)
        {
            return markdownMatch.Groups["title"].Value.Trim();
        }

        var anchorMatch = GodotReferenceAnchorPattern().Match(line);
        if (anchorMatch.Success)
        {
            var memberTitle = $"{anchorMatch.Groups["class"].Value}.{anchorMatch.Groups["name"].Value} ({anchorMatch.Groups["kind"].Value.Replace('_', ' ')})";
            var signatureLine = lines
                .Skip(lineIndex + 1)
                .Take(8)
                .FirstOrDefault(candidate => candidate.Trim().Length > 0 && !candidate.TrimStart().StartsWith("..", StringComparison.Ordinal));
            return signatureLine is null ? memberTitle : $"{memberTitle} — {CleanReStructuredText(signatureLine)}";
        }

        var trimmedLine = line.Trim();
        if (lineIndex + 1 < lines.Length && trimmedLine.Length > 0 && !IsReStructuredTextUnderline(trimmedLine) && !trimmedLine.StartsWith("..", StringComparison.Ordinal))
        {
            var nextLine = lines[lineIndex + 1].Trim();
            if (IsReStructuredTextUnderline(nextLine) && nextLine.Length >= trimmedLine.Length)
            {
                return trimmedLine;
            }
        }

        return null;
    }

    private static bool IsReStructuredTextUnderline(string trimmedLine)
    {
        return trimmedLine.Length >= 3 && "=-~^*+#\"'`:._".Contains(trimmedLine[0]) && trimmedLine.All(character => character == trimmedLine[0]);
    }

    /// <summary>
    /// Godot signature lines without the markup: ":ref:`Node&lt;class_Node&gt;`" becomes "Node", |void| becomes void,
    /// and the link icons, bold marks and escaped spaces go.
    /// </summary>
    private static string CleanReStructuredText(string signatureLine)
    {
        var cleanedText = ReferenceRolePattern().Replace(signatureLine, match => match.Groups["text"].Value);
        cleanedText = cleanedText.Replace("\\ ", "").Replace("**", "").Replace("``", "").Replace("🔗", "").Replace("|", "");
        cleanedText = Regex.Replace(cleanedText, @"\\(.)", "$1");
        cleanedText = Regex.Replace(cleanedText, @"\s+", " ").Trim();
        return cleanedText.Length > 160 ? cleanedText[..160] + "…" : cleanedText;
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
    /// .NET API reference files (dotnet/dotnet-api-docs, one &lt;Type&gt; per file): one chunk for the type's overview and one per member,
    /// titled with the member's C# signature ("List&lt;T&gt;.RemoveRange — public void RemoveRange (int index, int count);"),
    /// so a search for a method finds each real overload and its exact parameters.
    /// Returns null for any other XML.
    /// </summary>
    private static List<KnowledgeChunk>? SplitDotNetApiDocs(string text, string[] lines)
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

        var typeElement = document.Root;
        var memberElements = typeElement?.Element("Members")?.Elements("Member").ToList() ?? [];
        if (typeElement is null || typeElement.Name.LocalName != "Type" || memberElements.Count == 0)
        {
            return null;
        }

        var typeName = typeElement.Attribute("Name")?.Value ?? "type";
        var chunks = new List<KnowledgeChunk>();
        var firstMemberLine = ((IXmlLineInfo)memberElements[0]).LineNumber;
        AddSection(chunks, $"{typeName} — type overview ({typeElement.Attribute("FullName")?.Value ?? typeName})", lines, 0, firstMemberLine - 2);

        var membersClosingLine = Array.FindIndex(lines, firstMemberLine - 1, line => line.TrimStart().StartsWith("</Members>", StringComparison.Ordinal)) + 1;
        for (var memberIndex = 0; memberIndex < memberElements.Count; memberIndex++)
        {
            var memberElement = memberElements[memberIndex];
            var startLine = ((IXmlLineInfo)memberElement).LineNumber;
            var endLine = memberIndex + 1 < memberElements.Count ? ((IXmlLineInfo)memberElements[memberIndex + 1]).LineNumber - 1 : (membersClosingLine > 0 ? membersClosingLine - 1 : lines.Length);
            var csharpSignature = memberElement.Elements("MemberSignature").FirstOrDefault(signature => signature.Attribute("Language")?.Value == "C#")?.Attribute("Value")?.Value;
            var memberName = memberElement.Attribute("MemberName")?.Value ?? "member";
            var title = csharpSignature is null ? $"{typeName}.{memberName}" : $"{typeName}.{memberName} — {csharpSignature}";
            AddSection(chunks, title, lines, startLine - 1, Math.Max(startLine, endLine) - 1);
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

    [GeneratedRegex(@"^\.\. _class_(?<class>@?[A-Za-z0-9]+)_(?<kind>method|property|signal|constant|enum|annotation|constructor|operator|theme_item)_(?<name>.+):\s*$")]
    private static partial Regex GodotReferenceAnchorPattern();

    [GeneratedRegex(@":ref:`(?<text>[^`<]*?)\s*(<[^>]*>)?`")]
    private static partial Regex ReferenceRolePattern();

    [GeneratedRegex(@"\b(?<kind>class|struct|interface|enum|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclarationPattern();
}
