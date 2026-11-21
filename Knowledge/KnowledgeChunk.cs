namespace Rhodes.Core.Knowledge;

/// <summary>
/// One searchable piece of a knowledge file: a section of a doc, a window of code or one Def, with its line range (1-based, inclusive).
/// </summary>
public sealed record KnowledgeChunk(string Title, string Content, int StartLine, int EndLine);

/// <summary>
/// One search hit: where it is, what it's called, and a short snippet with the matched words in [brackets].
/// </summary>
public sealed record KnowledgeSearchResult(string RelativePath, string Collection, string Title, int StartLine, int EndLine, string Snippet, double Score);

/// <summary>
/// Exact lines from a knowledge file, 1-based, with the file's total line count.
/// </summary>
public sealed record KnowledgeLines(string RelativePath, int StartLine, int TotalLines, IReadOnlyList<string> Lines);
