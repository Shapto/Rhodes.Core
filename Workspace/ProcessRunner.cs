using System.Diagnostics;
using System.Text;

namespace Rhodes.Core.Workspace;

public sealed record ProcessResult(int? ExitCode, bool TimedOut, double Seconds, string Output);

/// <summary>
/// Runs a command line in a folder and collects what it prints (output and errors together, in order).
/// Long output keeps its start and its end, where build errors and test summaries are.
/// </summary>
public static class ProcessRunner
{
    private const int KeptStartCharacters = 2000;
    private const int KeptEndCharacters = 7000;

    public static async Task<ProcessResult> RunAsync(string commandLine, string workingFolder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (isWindows)
        {
            // cmd.exe doesn't understand .NET's \" escaping, so the line goes in raw: with /s, cmd strips exactly
            // the outer pair of quotes and runs what's inside as typed, quoted paths included.
            startInfo.Arguments = "/d /s /c \"" + commandLine + "\"";
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commandLine);
        }

        // Builds shouldn't stop to ask anything, and dotnet's first-run banner is noise.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

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
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArguments) => Collect(eventArguments.Data);
        process.ErrorDataReceived += (_, eventArguments) => Collect(eventArguments.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It finished on its own in the meantime.
            }

            if (!timedOut)
            {
                throw;
            }
        }

        string collectedOutput;
        lock (outputLock)
        {
            collectedOutput = output.ToString();
        }

        return new ProcessResult(timedOut ? null : process.ExitCode, timedOut, stopwatch.Elapsed.TotalSeconds, Shorten(collectedOutput));
    }

    private static string Shorten(string text)
    {
        if (text.Length <= KeptStartCharacters + KeptEndCharacters)
        {
            return text;
        }

        var droppedCharacters = text.Length - KeptStartCharacters - KeptEndCharacters;
        return text[..KeptStartCharacters] + $"\n[… {droppedCharacters:N0} characters of output left out …]\n" + text[^KeptEndCharacters..];
    }
}