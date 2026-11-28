using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhodes.Core.Approvals;
using Rhodes.Core.Settings;

namespace Rhodes.Core.Tools
{
    public sealed record HavenResult(int? ExitCode, bool TimedOut, bool Unreachable, double Seconds, string Output);

    /// <summary>
    /// Runs commands on haven (Shapto's home server) over SSH with the key this computer already uses for Shapto's
    /// account there. BatchMode means ssh never stops to ask for a password or a host key: if the key doesn't work,
    /// the command fails with ssh's message. A haven that's off is reported as off. Nothing here ever wakes it.
    /// The settings (Haven:Host, Haven:User, Haven:Port, Haven:DockerCommand) are read on every call, so changes on
    /// the home page apply right away.
    /// </summary>
    public sealed class HavenConnection
    {
        public const string HostSettingKey = "Haven:Host";
        public const string UserSettingKey = "Haven:User";
        public const string PortSettingKey = "Haven:Port";
        public const string DockerCommandSettingKey = "Haven:DockerCommand";

        private const int KeptStartCharacters = 2000;
        private const int KeptEndCharacters = 7000;

        // ssh exits with 255 for its own errors; these are the ones that mean haven isn't there.
        private static readonly string[] UnreachableMessages =
        [
            "Connection timed out", "No route to host", "Connection refused", "Could not resolve hostname",
            "Network is unreachable", "Host is down", "Name or service not known", "Operation timed out",
        ];

        private readonly LocalSettingsFile localSettingsFile;
        private readonly IConfiguration startupConfiguration;

        public HavenConnection(LocalSettingsFile localSettingsFile, IConfiguration startupConfiguration)
        {
            this.localSettingsFile = localSettingsFile;
            this.startupConfiguration = startupConfiguration;
        }

        public string Host => ReadSetting(HostSettingKey) ?? "haven";

        public string? User => ReadSetting(UserSettingKey);

        /// <summary>
        /// How docker is run on haven: "docker" when Shapto's user is in the docker group, or e.g. "sudo -n docker".
        /// </summary>
        public string DockerCommand => ReadSetting(DockerCommandSettingKey) ?? "docker";

        public string Target => User is { } userName ? userName + "@" + Host : Host;

        public async Task<HavenResult> RunAsync(string remoteCommand, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var sshOption in new[] { "BatchMode=yes", "ConnectTimeout=5", "ServerAliveInterval=15", "ServerAliveCountMax=2" })
            {
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(sshOption);
            }

            if (ReadSetting(PortSettingKey) is { } portText && int.TryParse(portText, out var port) && port > 0)
            {
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(port.ToString());
            }

            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(Target);
            startInfo.ArgumentList.Add(remoteCommand);

            var output = new StringBuilder();
            var outputLock = new object();
            void Collect(string? line)
            {
                if (line is null)
                {
                    return;
                }

                lock (outputLock)
                {
                    output.AppendLine(line);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            using var sshProcess = new Process { StartInfo = startInfo };
            sshProcess.OutputDataReceived += (_, lineEvent) => Collect(lineEvent.Data);
            sshProcess.ErrorDataReceived += (_, lineEvent) => Collect(lineEvent.Data);
            try
            {
                sshProcess.Start();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                return new HavenResult(null, false, false, 0, "ssh isn't installed on this computer: " + exception.Message);
            }

            sshProcess.BeginOutputReadLine();
            sshProcess.BeginErrorReadLine();
            // Nothing is typed into remote commands, so a command that waits for input ends instead of hanging.
            sshProcess.StandardInput.Close();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                await sshProcess.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                try
                {
                    sshProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already finished.
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            // Lets the last output lines arrive after the exit.
            if (!timedOut)
            {
                sshProcess.WaitForExit();
            }

            string outputText;
            lock (outputLock)
            {
                outputText = output.ToString();
            }

            var exitCode = timedOut ? (int?)null : sshProcess.ExitCode;
            var unreachable = exitCode == 255 && UnreachableMessages.Any(message => outputText.Contains(message, StringComparison.OrdinalIgnoreCase));
            return new HavenResult(exitCode, timedOut, unreachable, stopwatch.Elapsed.TotalSeconds, Shorten(outputText));
        }

        /// <summary>
        /// The text a tool returns when haven didn't answer at all, so she says it's off instead of guessing.
        /// </summary>
        public string DescribeUnreachable(HavenResult result)
        {
            return $"haven ({Target}) isn't reachable, so it's most likely turned off (it's only on during the day). " +
                "That's normal: say so and carry on. Don't ask Shapto to turn it on unless they asked for something that needs it.\n" +
                "ssh said: " + result.Output.Trim();
        }

        public static string DescribeSshFailure(HavenResult result)
        {
            return "Error: ssh couldn't log in to haven (exit 255). This is a setup problem for Shapto, not something to retry: " +
                "the key may not be loaded for the user the core runs as, or haven's host key isn't known yet " +
                "(running ssh once by hand fixes that).\n" + result.Output.Trim();
        }

        private string? ReadSetting(string settingKey)
        {
            var settingValue = localSettingsFile.Read(settingKey) ?? startupConfiguration[settingKey];
            return string.IsNullOrWhiteSpace(settingValue) ? null : settingValue.Trim();
        }

        private static string Shorten(string outputText)
        {
            if (outputText.Length <= KeptStartCharacters + KeptEndCharacters)
            {
                return outputText;
            }

            var cutCharacters = outputText.Length - KeptStartCharacters - KeptEndCharacters;
            return outputText[..KeptStartCharacters] + $"\n… ({cutCharacters} characters cut) …\n" + outputText[^KeptEndCharacters..];
        }
    }

    /// <summary>
    /// A read-only report of haven's state: uptime and load, memory, disks, GPU, temperatures, and its containers.
    /// Needs no approval, since it changes nothing.
    /// </summary>
    public sealed class HavenStatusTool : ICoreTool
    {
        private readonly HavenConnection havenConnection;

        public HavenStatusTool(HavenConnection havenConnection)
        {
            this.havenConnection = havenConnection;
        }

        public string Name => "haven_status";

        public string Description =>
            "Check on haven, Shapto's home server (Ubuntu Server with CasaOS and Docker, a GTX 1060, an SSD at /mnt/SSDStorage " +
            "and a storage pool at /DATA). Returns uptime, load, memory, disk use, GPU, temperatures and the containers. " +
            "Read-only, needs no approval. haven is usually off at night; if it doesn't answer, it's off.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            // One ssh call; each part prints a header and says so when its tool is missing, so one failure doesn't hide the rest.
            var dockerCommand = havenConnection.DockerCommand;
            var reportScript = string.Join("; ",
                "echo '## host'; hostname; uptime",
                "echo; echo '## memory'; free -h",
                "echo; echo '## disks'; df -h -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs 2>/dev/null",
                "echo; echo '## gpu'; (command -v nvidia-smi >/dev/null && nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw --format=csv) || echo 'nvidia-smi not found'",
                "echo; echo '## temperatures'; (command -v sensors >/dev/null && sensors 2>/dev/null | grep -E '^(Package|Core|temp|CPU|Tctl|Tdie)' ) || echo 'sensors not installed (lm-sensors)'",
                $"echo; echo '## containers'; {dockerCommand} ps -a --format '{{{{.Names}}}}\t{{{{.Status}}}}\t{{{{.Image}}}}' 2>&1");
            var result = await havenConnection.RunAsync(reportScript, TimeSpan.FromSeconds(30), cancellationToken);
            if (result.Unreachable)
            {
                return havenConnection.DescribeUnreachable(result);
            }

            if (result.ExitCode == 255)
            {
                return HavenConnection.DescribeSshFailure(result);
            }

            var report = result.Output.TrimEnd();
            if (report.Contains("permission denied", StringComparison.OrdinalIgnoreCase) && report.Contains("docker", StringComparison.OrdinalIgnoreCase))
            {
                report += "\n(Note: docker refused Shapto's user. The fix is on haven: \"sudo usermod -aG docker $USER\" and log in again, " +
                    "or set Haven:DockerCommand to \"sudo -n docker\" with passwordless sudo for docker.)";
            }

            return report.Length == 0 ? "(haven answered but printed nothing)" : report;
        }
    }

    /// <summary>
    /// haven's Docker containers (CasaOS apps are containers too): list them and read their logs freely;
    /// start, stop and restart after Shapto approves.
    /// </summary>
    public sealed class HavenContainersTool : ICoreTool, IApprovalRequired
    {
        private const int DefaultLogLines = 80;
        private const int MaximumLogLines = 500;

        private static readonly Regex ContainerNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$");
        private static readonly string[] ChangingActions = ["start", "stop", "restart"];

        private readonly HavenConnection havenConnection;

        public HavenContainersTool(HavenConnection havenConnection)
        {
            this.havenConnection = havenConnection;
        }

        public string Name => "haven_containers";

        public string Description =>
            "Work with haven's Docker containers (its CasaOS apps, e.g. Forgejo, Ollama, Syncthing, Scrutiny). " +
            "\"list\" shows them all, \"logs\" shows a container's recent log; both are free. " +
            "\"start\", \"stop\" and \"restart\" change it and Shapto approves each one. Use the exact name from \"list\".";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("list", "logs", "start", "stop", "restart") },
                ["container"] = new JsonObject { ["type"] = "string", ["description"] = "The container's name as \"list\" shows it. Not needed for \"list\"." },
                ["lines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumLogLines, ["description"] = "For \"logs\": how many of the newest lines. Default 80." },
            },
            ["required"] = new JsonArray("action"),
        };

        public ApprovalRequest? DescribeApproval(JsonElement arguments)
        {
            var action = ToolArguments.GetOptionalString(arguments, "action")?.Trim().ToLowerInvariant();
            var containerName = ToolArguments.GetOptionalString(arguments, "container")?.Trim();
            if (action is null || !ChangingActions.Contains(action) || containerName is null || !ContainerNamePattern.IsMatch(containerName))
            {
                return null;
            }

            // "Always allow" covers this action on this one container, never every container.
            var alwaysKey = $"haven: docker {action} {containerName}";
            return new ApprovalRequest(ApprovalCategories.Command, $"{Capitalize(action)} a container on haven", $"docker {action} {containerName}\non {havenConnection.Target}", alwaysKey, $"\"{action} {containerName}\" on haven");
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var action = ToolArguments.GetRequiredString(arguments, "action").Trim().ToLowerInvariant();
            var dockerCommand = havenConnection.DockerCommand;
            string remoteCommand;
            if (action == "list")
            {
                remoteCommand = $"{dockerCommand} ps -a --format 'table {{{{.Names}}}}\t{{{{.Status}}}}\t{{{{.Image}}}}\t{{{{.Ports}}}}'";
            }
            else
            {
                var containerName = ToolArguments.GetOptionalString(arguments, "container")?.Trim();
                if (string.IsNullOrEmpty(containerName) || !ContainerNamePattern.IsMatch(containerName))
                {
                    throw new ToolArgumentException("\"container\" needs a container name exactly as action \"list\" shows it (letters, digits, - _ .).");
                }

                remoteCommand = action switch
                {
                    "logs" => $"{dockerCommand} logs --tail {ToolArguments.GetOptionalInteger(arguments, "lines", DefaultLogLines, 1, MaximumLogLines)} --timestamps {containerName} 2>&1",
                    "start" or "stop" or "restart" => $"{dockerCommand} {action} {containerName} 2>&1 && {dockerCommand} ps -a --filter name=^/{containerName}$ --format '{{{{.Names}}}}: {{{{.Status}}}}'",
                    _ => throw new ToolArgumentException("\"action\" must be list, logs, start, stop or restart."),
                };
            }

            var result = await havenConnection.RunAsync(remoteCommand, TimeSpan.FromMinutes(2), cancellationToken);
            if (result.Unreachable)
            {
                return havenConnection.DescribeUnreachable(result);
            }

            if (result.ExitCode == 255)
            {
                return HavenConnection.DescribeSshFailure(result);
            }

            var outputText = result.Output.TrimEnd();
            if (result.TimedOut)
            {
                return "Stopped after 2 minutes without finishing.\n" + outputText;
            }

            return result.ExitCode == 0 ? (outputText.Length == 0 ? "Done (no output)." : outputText) : $"Error: exit code {result.ExitCode}.\n{outputText}";
        }

        private static string Capitalize(string word)
        {
            return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
        }
    }

    /// <summary>
    /// Runs one shell command on haven over SSH after Shapto approves it, and returns its output and exit code.
    /// </summary>
    public sealed class HavenRunCommandTool : ICoreTool, IApprovalRequired
    {
        private const int DefaultTimeoutSeconds = 60;
        private const int MaximumTimeoutSeconds = 900;

        private readonly HavenConnection havenConnection;

        public HavenRunCommandTool(HavenConnection havenConnection)
        {
            this.havenConnection = havenConnection;
        }

        public string Name => "haven_run_command";

        public string Description =>
            "Run a shell command on haven (Shapto's home server, Ubuntu Server, logged in as Shapto) over SSH and get its output and exit code. " +
            "Shapto approves each command. Use haven_status and haven_containers when they cover it. " +
            "Nothing that needs a password works (no interactive sudo), and nothing is typed in, so pass everything as arguments. " +
            "Prefer moving things somewhere safe over deleting them.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The command line, run by haven's shell in Shapto's home folder, e.g. \"du -sh /DATA/*\"." },
                ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 5, ["maximum"] = MaximumTimeoutSeconds, ["description"] = "Stop it after this long. Default 60." },
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

            // "Always allow" remembers the program and its first word, marked as haven's so it never covers workspace commands.
            var commandStart = string.Join(' ', commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            return new ApprovalRequest(ApprovalCategories.Command, "Run a command on haven", $"{commandLine}\non {havenConnection.Target}", "haven: " + commandStart, $"\"{commandStart}\" on haven");
        }

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var commandLine = ToolArguments.GetRequiredString(arguments, "command").Trim();
            var timeoutSeconds = ToolArguments.GetOptionalInteger(arguments, "timeout_seconds", DefaultTimeoutSeconds, 5, MaximumTimeoutSeconds);
            var result = await havenConnection.RunAsync(commandLine, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            if (result.Unreachable)
            {
                return havenConnection.DescribeUnreachable(result);
            }

            if (result.ExitCode == 255 && result.Seconds < 6 && result.Output.Contains("Permission denied", StringComparison.Ordinal) && result.Output.Contains("publickey", StringComparison.Ordinal))
            {
                return HavenConnection.DescribeSshFailure(result);
            }

            var builder = new StringBuilder();
            builder.AppendLine(result.TimedOut ? $"Stopped after {timeoutSeconds} s without finishing." : $"Exit code {result.ExitCode} after {result.Seconds:0.0} s.");
            builder.Append(result.Output.Trim().Length == 0 ? "(no output)" : result.Output.TrimEnd());
            return builder.ToString();
        }
    }
}
