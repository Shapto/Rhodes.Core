using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Approvals;
using Rhodes.Core.Workspace;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Runs a command in a workspace folder (dotnet build, dotnet test, git status, …) after Shapto approves it,
    /// and returns what it printed. The folder only sets where the command starts: it's Shapto's approval that keeps it safe.
    /// </summary>
    public sealed class RunCommandTool : ICoreTool, IApprovalRequired
    {
        private const int DefaultTimeoutSeconds = 180;
        private const int MaximumTimeoutSeconds = 900;

        private readonly WorkspaceFolder workspaceFolder;

        public RunCommandTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "run_command";

        public string Description =>
            "Run a command in a folder of your workspace, e.g. \"dotnet build\" or \"dotnet test\", and get its output and exit code. " +
            "Runs in cmd.exe on Windows 11: use Windows commands (dir, type, findstr, del), not Linux ones (ls, cat, grep, rm). " +
            "Shapto approves each command. Only for building, testing and inspecting your workspace projects; nothing outside it.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The command line, e.g. \"dotnet build -v q\"." },
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Folder relative to the workspace root, exactly as list_files shows it, e.g. \"roman-test\" or \"Gungnir\". Never a full path or one starting with Data\\workspace. Leave out for the workspace root itself." },
                ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 10, ["maximum"] = MaximumTimeoutSeconds, ["description"] = "Stop it after this long. Default 180." },
            },
            ["required"] = new JsonArray("command"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            var commandLine = ToolArguments.GetOptionalString(arguments, "command")?.Trim();
            if (string.IsNullOrEmpty(commandLine))
            {
                return null;
            }

            string folderPath;
            try
            {
                folderPath = workspaceFolder.Resolve(ToolArguments.GetOptionalString(arguments, "folder"));
            }
            catch (ToolArgumentException)
            {
                return null;
            }

            // "Always allow" remembers the program and its first word ("dotnet build"), never the whole line.
            var commandStart = string.Join(' ', commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            return new ApprovalRequest(ApprovalCategories.Command, "Run a command", $"{commandLine}\nin workspace/{workspaceFolder.ToRelative(folderPath)}", commandStart, "\"" + commandStart + "\"");
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var commandLine = ToolArguments.GetRequiredString(arguments, "command").Trim();
            var folderPath = workspaceFolder.Resolve(ToolArguments.GetOptionalString(arguments, "folder"));
            if (!Directory.Exists(folderPath))
            {
                throw new ToolArgumentException($"There's no folder \"{workspaceFolder.ToRelative(folderPath)}\". Use list_files to see what's there.");
            }

            var timeoutSeconds = ToolArguments.GetOptionalInteger(arguments, "timeout_seconds", DefaultTimeoutSeconds, 10, MaximumTimeoutSeconds);
            var result = await ProcessRunner.RunAsync(commandLine, folderPath, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            var builder = new StringBuilder();
            builder.AppendLine(result.TimedOut ? $"Stopped after {timeoutSeconds} s without finishing." : $"Exit code {result.ExitCode} after {result.Seconds:0.0} s.");
            builder.Append(result.Output.Length == 0 ? "(no output)" : result.Output.TrimEnd());
            return builder.ToString();
        }
    }

    /// <summary>
    /// Pulls a git repository into the workspace (a shallow clone: the latest version, without history).
    /// Approved like a web fetch, by its site.
    /// </summary>
    public sealed class CloneRepoTool : ICoreTool, IApprovalRequired
    {
        private readonly WorkspaceFolder workspaceFolder;

        public CloneRepoTool(WorkspaceFolder workspaceFolder)
        {
            this.workspaceFolder = workspaceFolder;
        }

        public string Name => "clone_repo";

        public string Description =>
            "Copy a git repository (e.g. a GitHub link) into your workspace so you can read, change and build it. " +
            "Gets the latest version only. Shapto approves the site.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The repository, e.g. \"https://github.com/Shapto/Rhodes.Core\"." },
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Workspace folder to put it in. Default: the repository's name." },
            },
            ["required"] = new JsonArray("url"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            var url = ToolArguments.GetOptionalString(arguments, "url")?.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return new ApprovalRequest(ApprovalCategories.Web, "Clone a repository into the workspace", uri.ToString(), host, host);
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var url = ToolArguments.GetRequiredString(arguments, "url").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ToolArgumentException("\"url\" must be a full https:// address of a git repository.");
            }

            var repositoryName = Path.GetFileNameWithoutExtension(uri.AbsolutePath.TrimEnd('/'));
            var targetPath = workspaceFolder.Resolve(ToolArguments.GetOptionalString(arguments, "folder") ?? repositoryName);
            if (targetPath.Contains('"'))
            {
                throw new ToolArgumentException("The folder name can't contain quotes.");
            }

            if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            {
                throw new ToolArgumentException($"workspace/{workspaceFolder.ToRelative(targetPath)} already exists. Pick another folder, or work in the copy that's there.");
            }

            var commandLine = $"git clone --depth 1 \"{uri}\" \"{targetPath}\"";
            var result = await ProcessRunner.RunAsync(commandLine, workspaceFolder.RootPath, TimeSpan.FromMinutes(5), cancellationToken);
            if (result.TimedOut || result.ExitCode != 0)
            {
                return $"Error: the clone failed{(result.TimedOut ? " (took over 5 minutes)" : $" (exit code {result.ExitCode})")}. Is git installed and the address right?\n{result.Output.TrimEnd()}";
            }

            return $"Cloned into workspace/{workspaceFolder.ToRelative(targetPath)} in {result.Seconds:0} s. Use list_files to look around.";
        }
    }
}