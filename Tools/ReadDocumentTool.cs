using Rhodes.Core.Documents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Exact lines from a big document, with numbers, for reading in order or around a search hit.
    /// </summary>
    public sealed class ReadDocumentTool : ICoreTool
    {
        private const int DefaultLineCount = 120;
        private const int MaximumLineCount = 400;
        private const int MaximumCharacters = 14000;

        private readonly DocumentStore documentStore;

        public ReadDocumentTool(DocumentStore documentStore)
        {
            this.documentStore = documentStore;
        }

        public string Name => "read_document";

        public string Description =>
        "Read exact lines of a big document (an attached file or a long page you fetched), with line numbers. " +
        "Use it to read on, or around a part search_document found. Default 120 lines from start_line.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["document_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The document's id, 12 characters.",
                },
                ["start_line"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "First line to read. Default 1.",
                },
                ["line_count"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = MaximumLineCount,
                    ["description"] = "How many lines. Default 120.",
                },
            },
            ["required"] = new JsonArray("document_id"),
        };

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var documentId = ToolArguments.GetRequiredString(arguments, "document_id").Trim().ToLowerInvariant();
            var startLine = ToolArguments.GetOptionalInteger(arguments, "start_line", defaultValue: 1, minimum: 1, maximum: int.MaxValue);
            var lineCount = ToolArguments.GetOptionalInteger(arguments, "line_count", defaultValue: DefaultLineCount, minimum: 1, maximum: MaximumLineCount);

            var documentLines = documentStore.ReadLines(documentId, startLine, lineCount);
            if (documentLines is null)
            {
                throw new ToolArgumentException($"There's no document \"{documentId}\". Use the 12-character id from the file's note or web_fetch's result.");
            }

            var document = documentLines.Document;
            if (documentLines.Lines.Count == 0)
            {
                return Task.FromResult($"{document.Name} has only {document.LineCount:N0} lines; start_line {startLine} is past the end.");
            }

            // Very long lines (minified files) could still overflow her context, so the reply is capped by size too.
            var shownLines = new List<string>();
            var shownCharacters = 0;
            foreach (var line in documentLines.Lines)
            {
                if (shownLines.Count > 0 && shownCharacters + line.Length > MaximumCharacters)
                {
                    break;
                }

                shownLines.Add(line.Length > MaximumCharacters ? line[..MaximumCharacters] + " [line cut]" : line);
                shownCharacters += line.Length;
            }

            var lastLine = startLine + shownLines.Count - 1;
            var builder = new StringBuilder();
            builder.AppendLine($"{document.Name}, lines {startLine}–{lastLine} of {document.LineCount:N0}:");
            builder.Append(DocumentStore.NumberLines(startLine, shownLines));
            builder.Append(lastLine < document.LineCount ? $"[Continue with start_line {lastLine + 1}.]" : "[End of the document.]");
            return Task.FromResult(builder.ToString());
        }
    }
}