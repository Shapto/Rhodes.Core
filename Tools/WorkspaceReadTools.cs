using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Documents;
using Rhodes.Core.Workspace;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// What's in the workspace: folders and files a couple of levels deep, skipping build output and .git.
    /// </summary>
    public sealed class ListFilesTool : ICoreTool
    {
        private const int MaximumEntries = 300;

        private readonly WorkspaceFolder workspaceFolder;

        public ListFilesTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "list_files";

        public string Description =>
            "List folders and files in your workspace (your sandbox for code). Leave out path for the top level. " +
            "Build output, .git and similar folders are skipped.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A folder inside the workspace, e.g. \"Gungnir/Source\". Leave out for the top." },
                ["depth"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 6, ["description"] = "How many levels down to list. Default 2." },
            },
        };

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var folderPath = workspaceFolder.Resolve(ToolArguments.GetOptionalString(arguments, "path"));
            var depth = ToolArguments.GetOptionalInteger(arguments, "depth", 2, 1, 6);
            if (!Directory.Exists(folderPath))
            {
                throw new ToolArgumentException($"There's no folder \"{workspaceFolder.ToRelative(folderPath)}\" in the workspace. list_files without a path shows what's there.");
            }

            var entries = new List<string>();
            AddEntries(folderPath, depth, "", entries);
            var shownName = workspaceFolder.ToRelative(folderPath);
            var builder = new StringBuilder();
            builder.AppendLine($"workspace/{shownName}{(shownName.Length > 0 ? "/" : "")}:");
            if (entries.Count == 0)
            {
                builder.AppendLine("  (empty)");
            }

            foreach (var entry in entries.Take(MaximumEntries))
            {
                builder.AppendLine(entry);
            }

            if (entries.Count > MaximumEntries)
            {
                builder.AppendLine($"  … and {entries.Count - MaximumEntries} more. List a subfolder to see them.");
            }

            return Task.FromResult(builder.ToString().TrimEnd());
        }

        private static void AddEntries(string folderPath, int depthLeft, string indent, List<string> entries)
        {
            if (entries.Count > MaximumEntries)
            {
                return;
            }

            foreach (var subfolderPath in Directory.GetDirectories(folderPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var subfolderName = Path.GetFileName(subfolderPath);
                if (WorkspaceFolder.IsSkippedFolder(subfolderName) || new DirectoryInfo(subfolderPath).LinkTarget is not null)
                {
                    continue;
                }

                entries.Add($"{indent}  {subfolderName}/");
                if (depthLeft > 1)
                {
                    AddEntries(subfolderPath, depthLeft - 1, indent + "  ", entries);
                }
            }

            foreach (var filePath in Directory.GetFiles(folderPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add($"{indent}  {Path.GetFileName(filePath)} ({DescribeSize(new FileInfo(filePath).Length)})");
            }
        }

        private static string DescribeSize(long bytes)
        {
            return bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";
        }
    }

    /// <summary>
    /// Reads lines of a file in the workspace, numbered, so edit_file and Shapto can point at exact lines.
    /// </summary>
    public sealed class ReadFileTool : ICoreTool
    {
        private const int DefaultLineCount = 250;
        private const int MaximumLineCount = 500;
        private const int MaximumCharacters = 20000;
        private const long MaximumFileBytes = 5 * 1024 * 1024;

        private readonly WorkspaceFolder workspaceFolder;

        public ReadFileTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "read_file";

        public string Description => "Read a file in your workspace, with line numbers. Long files come in parts: use start_line to continue.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file, e.g. \"Gungnir/Source/Gungnir.cs\"." },
                ["start_line"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "First line to read. Default 1." },
                ["line_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumLineCount, ["description"] = "How many lines. Default 250." },
            },
            ["required"] = new JsonArray("path"),
        };

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var filePath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
            var shownPath = workspaceFolder.ToRelative(filePath);
            if (!File.Exists(filePath))
            {
                throw new ToolArgumentException($"There's no file \"{shownPath}\". Use list_files to see what's there.");
            }

            if (new FileInfo(filePath).Length > MaximumFileBytes)
            {
                throw new ToolArgumentException($"{shownPath} is over 5 MB, too big to read here.");
            }

            var lines = DocumentStore.SplitLines(await File.ReadAllTextAsync(filePath, cancellationToken));
            var startLine = ToolArguments.GetOptionalInteger(arguments, "start_line", 1, 1, int.MaxValue);
            var lineCount = ToolArguments.GetOptionalInteger(arguments, "line_count", DefaultLineCount, 1, MaximumLineCount);
            if (startLine > lines.Length)
            {
                return $"{shownPath} has only {lines.Length:N0} lines.";
            }

            var shownLines = new List<string>();
            var shownCharacters = 0;
            foreach (var line in lines.Skip(startLine - 1).Take(lineCount))
            {
                if (shownLines.Count > 0 && shownCharacters + line.Length > MaximumCharacters)
                {
                    break;
                }

                shownLines.Add(line);
                shownCharacters += line.Length;
            }

            var lastLine = startLine + shownLines.Count - 1;
            var builder = new StringBuilder();
            builder.AppendLine($"{shownPath}, lines {startLine}–{lastLine} of {lines.Length:N0}:");
            builder.Append(DocumentStore.NumberLines(startLine, shownLines));
            builder.Append(lastLine < lines.Length ? $"[Continue with start_line {lastLine + 1}.]" : "[End of the file.]");
            return builder.ToString();
        }
    }
}