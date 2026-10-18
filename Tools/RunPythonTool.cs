using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Sandbox;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Runs Python in the sandbox for exact math and data work: no network, no files outside its own scratch folder,
    /// no other programs. Needs no approval because it can't change anything.
    /// </summary>
    public sealed class RunPythonTool : ICoreTool
    {
        private const int DefaultTimeoutSeconds = 30;
        private const int MaximumTimeoutSeconds = 120;

        private readonly PythonSandbox pythonSandbox;

        public RunPythonTool(PythonSandbox pythonSandbox)
        {
            this.pythonSandbox = pythonSandbox;
        }

        public string Name => "run_python";

        public string Description =>
            "Run Python for exact math, statistics, unit conversions or data crunching, instead of doing arithmetic in your head. " +
            "Print what you need. numpy and sympy may be installed. Sandboxed: no internet, no files outside its own scratch folder, " +
            "no other programs. Not for building or running projects; use run_command in the workspace for that.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["code"] = new JsonObject { ["type"] = "string", ["description"] = "The Python code. Use print() for results." },
                ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 5, ["maximum"] = MaximumTimeoutSeconds, ["description"] = "Stop it after this long. Default 30." },
            },
            ["required"] = new JsonArray("code"),
        };

        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!pythonSandbox.IsAvailable)
            {
                return $"Error: Python isn't set up for the sandbox (looked for {pythonSandbox.PythonPath}). Tell Shapto; for now do the math step by step and say it's unchecked.";
            }

            var code = ToolArguments.GetRequiredString(arguments, "code");
            var timeoutSeconds = ToolArguments.GetOptionalInteger(arguments, "timeout_seconds", DefaultTimeoutSeconds, 5, MaximumTimeoutSeconds);
            var result = await pythonSandbox.RunAsync(code, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            var builder = new StringBuilder();
            builder.AppendLine(result.TimedOut ? $"Stopped after {timeoutSeconds} s without finishing." : result.ExitCode == 0 ? $"Ran in {result.Seconds:0.0} s." : $"Failed (exit code {result.ExitCode}) after {result.Seconds:0.0} s.");
            builder.Append(result.Output.Trim().Length == 0 ? "(no output; use print())" : result.Output.TrimEnd());
            return builder.ToString();
        }
    }
}
