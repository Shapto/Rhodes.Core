using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Approvals;
using Rhodes.Core.Workspace;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Creates or replaces a whole file in the workspace. Shapto approves it first, seeing what changes.
    /// </summary>
    public sealed class WriteFileTool : ICoreTool, IApprovalRequired
    {
        private const int MaximumContentCharacters = 200_000;

        private readonly WorkspaceFolder workspaceFolder;

        public WriteFileTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "write_file";

        public string Description =>
            "Create a file in your workspace, or replace one completely. Shapto approves each write. " +
            "For a change to part of an existing file, use edit_file instead; it's smaller and easier to approve.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative to the workspace root, exactly as list_files shows it, e.g. \"Gungnir/Source/Recall.cs\". Never a full path or one starting with Data\\workspace. Folders are created as needed." },
                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "The whole file." },
            },
            ["required"] = new JsonArray("path", "content"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            var (filePath, content) = TryRead(arguments);
            if (filePath is null || content is null)
            {
                return null;
            }

            var existingText = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
            var projectKey = workspaceFolder.ProjectKey(filePath);
            return new ApprovalRequest(ApprovalCategories.Write, (existingText is null ? "Create " : "Replace ") + workspaceFolder.ToRelative(filePath), TextDiff.Describe(existingText, content), projectKey, "writes in " + projectKey);
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var filePath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
            var content = ToolArguments.GetOptionalString(arguments, "content") ?? throw new ToolArgumentException("Missing \"content\": the whole file.");
            if (content.Length > MaximumContentCharacters)
            {
                throw new ToolArgumentException("That's over 200,000 characters. Split it into several files.");
            }

            if (Directory.Exists(filePath))
            {
                throw new ToolArgumentException("That path is a folder. Give a file name inside it.");
            }

            var existed = File.Exists(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false), cancellationToken);
            var lineCount = content.Split('\n').Length;
            return $"{(existed ? "Replaced" : "Created")} {workspaceFolder.ToRelative(filePath)} ({lineCount} lines).";
        }

        private (string? FilePath, string? Content) TryRead(JsonElement arguments)
        {
            try
            {
                var filePath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
                return Directory.Exists(filePath) ? (null, null) : (filePath, ToolArguments.GetOptionalString(arguments, "content"));
            }
            catch (ToolArgumentException)
            {
                return (null, null);
            }
        }
    }

    /// <summary>
    /// Replaces one exact piece of text in a workspace file. The old text has to appear exactly once, so the edit can't land in the wrong place.
    /// </summary>
    public sealed class EditFileTool : ICoreTool, IApprovalRequired
    {
        private readonly WorkspaceFolder workspaceFolder;

        public EditFileTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "edit_file";

        public string Description =>
            "Change part of a file in your workspace: old_text (copied exactly, including indentation, and appearing only once) " +
            "is replaced by new_text. Read the file first. Shapto approves each edit.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file to change, relative to the workspace root exactly as list_files shows it, e.g. \"roman-test/roman.py\". Never a full path or one starting with Data\\workspace." },
                ["old_text"] = new JsonObject { ["type"] = "string", ["description"] = "The exact text to replace; include a line or two around it so it's unique." },
                ["new_text"] = new JsonObject { ["type"] = "string", ["description"] = "What goes there instead. Empty to delete old_text." },
            },
            ["required"] = new JsonArray("path", "old_text", "new_text"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            try
            {
                var (filePath, oldText, newFileText, _, _) = PrepareEdit(arguments);
                var projectKey = workspaceFolder.ProjectKey(filePath);
                return new ApprovalRequest(ApprovalCategories.Write, "Edit " + workspaceFolder.ToRelative(filePath), TextDiff.Describe(oldText, newFileText), projectKey, "writes in " + projectKey);
            }
            catch (ToolArgumentException)
            {
                // ExecuteAsync explains the mistake to her; there's nothing to approve.
                return null;
            }
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var (filePath, _, newFileText, changeStartIndex, newTextLength) = PrepareEdit(arguments);
            await File.WriteAllTextAsync(filePath, newFileText, new UTF8Encoding(false), cancellationToken);
            return $"Edited {workspaceFolder.ToRelative(filePath)}. The changed part now reads:\n" + DescribeChangedLines(newFileText, changeStartIndex, newTextLength);
        }

        /// <summary>
        /// The changed lines plus a few around them, numbered, so she can see her edit landed where she meant it to.
        /// </summary>
        private static string DescribeChangedLines(string fileText, int changeStartIndex, int newTextLength)
        {
            const int ContextLines = 3;
            var lines = fileText.Replace("\r\n", "\n").Split('\n');
            var firstChangedLine = fileText[..changeStartIndex].Count(character => character == '\n');
            var lastChangedLine = firstChangedLine + fileText.Substring(changeStartIndex, newTextLength).Count(character => character == '\n');
            var firstShownLine = Math.Max(0, firstChangedLine - ContextLines);
            var lastShownLine = Math.Min(lines.Length - 1, lastChangedLine + ContextLines);

            var builder = new StringBuilder();
            for (var lineIndex = firstShownLine; lineIndex <= lastShownLine; lineIndex++)
            {
                builder.Append(lineIndex + 1).Append('\t').AppendLine(lines[lineIndex]);
            }
            return builder.ToString();
        }

        /// <summary>
        /// The file, its current text and its text after the edit. Line endings are matched to the file's own,
        /// so a Windows file keeps its \r\n even when she writes \n.
        /// </summary>
        private (string FilePath, string OldFileText, string NewFileText, int ChangeStartIndex, int NewTextLength) PrepareEdit(JsonElement arguments)
        {
            var filePath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
            if (!File.Exists(filePath))
            {
                throw new ToolArgumentException($"There's no file \"{workspaceFolder.ToRelative(filePath)}\". Use write_file to create it.");
            }

            var fileText = File.ReadAllText(filePath);
            var usesWindowsLineEndings = fileText.Contains("\r\n");
            string MatchLineEndings(string text) => usesWindowsLineEndings ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text.Replace("\r\n", "\n");

            var oldText = MatchLineEndings(ToolArguments.GetOptionalString(arguments, "old_text") ?? "");
            var newText = MatchLineEndings(ToolArguments.GetOptionalString(arguments, "new_text") ?? "");
            if (oldText.Length == 0)
            {
                throw new ToolArgumentException("\"old_text\" can't be empty. Copy the exact text you want to replace from read_file.");
            }

            var firstIndex = fileText.IndexOf(oldText, StringComparison.Ordinal);
            if (firstIndex < 0)
            {
                throw new ToolArgumentException("old_text isn't in the file exactly as written (check spaces and indentation). read_file the part again and copy it.");
            }

            if (fileText.IndexOf(oldText, firstIndex + oldText.Length, StringComparison.Ordinal) >= 0)
            {
                throw new ToolArgumentException("old_text appears more than once. Include a line or two around it so it's unique.");
            }

            return (filePath, fileText, fileText[..firstIndex] + newText + fileText[(firstIndex + oldText.Length)..], firstIndex, newText.Length);
        }
    }

    /// <summary>
    /// Deletes a file or folder in the workspace, after Shapto approves it.
    /// </summary>
    public sealed class DeleteFileTool : ICoreTool, IApprovalRequired
    {
        private readonly WorkspaceFolder workspaceFolder;

        public DeleteFileTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "delete_file";

        public string Description => "Delete a file, or a folder with everything in it, from your workspace. Shapto approves each delete.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file or folder to delete, relative to the workspace root exactly as list_files shows it, e.g. \"roman-test/roman.py\". Never a full path or one starting with Data\\workspace." },
            },
            ["required"] = new JsonArray("path"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            try
            {
                var targetPath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
                if (targetPath == workspaceFolder.RootPath || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
                {
                    return null;
                }

                var detail = Directory.Exists(targetPath) ? $"The folder and the {Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories).Count():N0} files in it." : "The file.";
                var projectKey = workspaceFolder.ProjectKey(targetPath);
                return new ApprovalRequest(ApprovalCategories.Write, "Delete " + workspaceFolder.ToRelative(targetPath), detail, projectKey, "writes in " + projectKey);
            }
            catch (ToolArgumentException)
            {
                return null;
            }
        }

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var targetPath = workspaceFolder.Resolve(ToolArguments.GetRequiredString(arguments, "path"));
            if (targetPath == workspaceFolder.RootPath)
            {
                throw new ToolArgumentException("That's the whole workspace. Delete the folders inside it one by one if you really mean it.");
            }

            var shownPath = workspaceFolder.ToRelative(targetPath);
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
                return Task.FromResult($"Deleted the folder {shownPath}.");
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                return Task.FromResult($"Deleted {shownPath}.");
            }

            throw new ToolArgumentException($"There's nothing at \"{shownPath}\".");
        }
    }
}