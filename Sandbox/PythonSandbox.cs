using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rhodes.Core.Sandbox;

public sealed record PythonRunResult(int? ExitCode, bool TimedOut, double Seconds, string Output);

/// <summary>
/// Runs Python for Closure's math, walled off from the machine. Three layers:
/// an audit hook inside Python (no network, no other programs, no native code, no files outside its run folder),
/// a Windows Job Object around the process (no child processes at all, a memory cap, killed with the job),
/// and a firewall rule on the sandbox's own python.exe that blocks the network (added once by setup-sandbox.bat).
/// Each run gets a fresh folder under Data\python-sandbox, deleted afterwards.
/// </summary>
public sealed class PythonSandbox
{
    private const int KeptOutputCharacters = 8000;
    private const long MemoryLimitBytes = 1024L * 1024 * 1024;

    private readonly string pythonPath;
    private readonly string sandboxFolder;
    private readonly string runnerPath;

    public PythonSandbox(string pythonPath, string sandboxFolder)
    {
        this.pythonPath = pythonPath;
        this.sandboxFolder = Path.GetFullPath(sandboxFolder);
        Directory.CreateDirectory(this.sandboxFolder);
        runnerPath = Path.Combine(this.sandboxFolder, "sandbox_runner.py");
        File.WriteAllText(runnerPath, RunnerScript, new UTF8Encoding(false));
    }

    public bool IsAvailable => File.Exists(pythonPath);

    public string PythonPath => pythonPath;

    public async Task<PythonRunResult> RunAsync(string code, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var runFolder = Path.Combine(sandboxFolder, "run-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(runFolder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(runFolder, "main.py"), code, new UTF8Encoding(false), cancellationToken);
            return await RunProcessAsync(runFolder, timeout, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(runFolder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open by a dying process; the next start cleans the folder.
            }
        }
    }

    private async Task<PythonRunResult> RunProcessAsync(string runFolder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = runFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("utf8");
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add(runFolder);

        // Only what Python needs; nothing from the core's own environment (tokens, paths) is passed on.
        startInfo.Environment.Clear();
        foreach (var variableName in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            if (Environment.GetEnvironmentVariable(variableName) is { } value)
            {
                startInfo.Environment[variableName] = variableName is "TEMP" or "TMP" ? runFolder : value;
            }
        }

        startInfo.Environment["MPLCONFIGDIR"] = runFolder;
        startInfo.Environment["MPLBACKEND"] = "Agg";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

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
                if (output.Length < KeptOutputCharacters * 4)
                {
                    output.AppendLine(line);
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArguments) => Collect(eventArguments.Data);
        process.ErrorDataReceived += (_, eventArguments) => Collect(eventArguments.Data);
        process.Start();
        using var jobObject = OperatingSystem.IsWindows() ? WindowsJobObject.Confine(process, MemoryLimitBytes) : null;
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
                // Already gone.
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

        // Python's own traceback lines point into the runner; the user code's lines are what matter.
        collectedOutput = collectedOutput.Replace(runnerPath, "<sandbox>");
        if (collectedOutput.Length > KeptOutputCharacters)
        {
            collectedOutput = collectedOutput[..2000] + "\n[… output shortened …]\n" + collectedOutput[^(KeptOutputCharacters - 2000)..];
        }

        return new PythonRunResult(timedOut ? null : process.ExitCode, timedOut, stopwatch.Elapsed.TotalSeconds, collectedOutput);
    }

    /// <summary>
    /// The script that runs inside Python before the user's code: it installs the audit hook, then runs main.py.
    /// Written next to the runs on every start, so it can't be edited to loosen the rules.
    /// </summary>
    private const string RunnerScript = """
# Rhodes python sandbox runner. Installs an audit hook that blocks the network, other programs, native code loading by
# the user's code, and any file access outside the run folder (reading Python's own install is allowed), then runs main.py.
import os
import sys

run_folder = os.path.realpath(sys.argv[1])
script_path = os.path.join(run_folder, "main.py")
read_roots = {run_folder}
for path_entry in [sys.prefix, sys.base_prefix, sys.exec_prefix, *sys.path]:
    if path_entry and os.path.isdir(path_entry):
        read_roots.add(os.path.realpath(path_entry))
read_roots = tuple(sorted(read_roots))

blocked_prefixes = (
    "socket.", "subprocess.", "os.system", "os.exec", "os.spawn", "os.posix_spawn", "os.startfile", "os.fork",
    "os.kill", "winreg.", "_winapi.", "webbrowser.", "urllib.", "http.client.", "ftplib.",
    "smtplib.", "poplib.", "imaplib.", "nntplib.", "telnetlib.", "msvcrt.", "ctypes.call_function", "ctypes.dlsym",
    "sys.remote_exec", "pty.", "resource.setrlimit",
)
path_events_write = {"os.remove", "os.rename", "os.rmdir", "os.mkdir", "os.chmod", "os.chown", "os.truncate", "os.link", "os.symlink", "os.utime", "shutil.copyfile", "shutil.copytree", "shutil.move", "shutil.rmtree", "shutil.make_archive", "shutil.unpack_archive"}
path_events_read = {"os.listdir", "os.scandir", "os.chdir", "glob.glob", "glob.glob/2"}


def inside(path, roots):
    try:
        resolved = os.path.realpath(os.fsdecode(path))
    except Exception:
        return False
    return any(resolved == root or resolved.startswith(root + os.sep) for root in roots)


def deny(what):
    raise PermissionError("sandbox: " + what + " is not allowed here. Only plain computation and files in your own run folder work.")


def audit(event, args):
    if event == "open":
        path, mode = args[0], args[1]
        if path is None or isinstance(path, int):
            return
        writing = mode is not None and any(flag in str(mode) for flag in "wax+")
        if writing and not inside(path, (run_folder,)):
            deny("writing " + str(path))
        if not writing and not inside(path, read_roots):
            deny("reading " + str(path))
    elif event in path_events_write:
        for argument in args:
            if isinstance(argument, (str, bytes, os.PathLike)) and not inside(argument, (run_folder,)):
                deny(event + " outside the run folder")
    elif event in path_events_read:
        if args and isinstance(args[0], (str, bytes, os.PathLike)) and not inside(args[0], read_roots):
            deny(event + " outside the run folder")
    elif event == "ctypes.dlopen":
        name = args[0] if args else None
        if name not in (None, "", b""):
            deny("loading native code")
    elif event.startswith(blocked_prefixes):
        deny(event)


with open(script_path, "r", encoding="utf-8") as script_file:
    source = script_file.read()
code = compile(source, "main.py", "exec")
os.chdir(run_folder)
sys.addaudithook(audit)
sys.argv = ["main.py"]
exec(code, {"__name__": "__main__", "__builtins__": __builtins__})
""";

    /// <summary>
    /// A Windows Job Object holding the sandbox process: it can't start any other process, can't use more than the memory cap,
    /// and dies with the job. Elsewhere (development on Linux) the audit hook is the only layer.
    /// </summary>
    private sealed class WindowsJobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint LimitActiveProcess = 0x00000008;
        private const uint LimitJobMemory = 0x00000200;
        private const uint LimitKillOnJobClose = 0x00002000;
        private const uint LimitDieOnUnhandledException = 0x00000400;

        private readonly IntPtr jobHandle;

        private WindowsJobObject(IntPtr jobHandle)
        {
            this.jobHandle = jobHandle;
        }

        public static WindowsJobObject Confine(Process process, long memoryLimitBytes)
        {
            var jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var limits = new JobObjectExtendedLimits
            {
                BasicLimits = new JobObjectBasicLimits
                {
                    LimitFlags = LimitActiveProcess | LimitJobMemory | LimitKillOnJobClose | LimitDieOnUnhandledException,
                    ActiveProcessLimit = 1,
                },
                JobMemoryLimit = (UIntPtr)memoryLimitBytes,
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimits>();
            var limitsPointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, false);
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, limitsPointer, (uint)size) || !AssignProcessToJobObject(jobHandle, process.Handle))
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    CloseHandle(jobHandle);
                    process.Kill(entireProcessTree: true);
                    throw new Win32Exception(errorCode, "Couldn't confine the sandbox process");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(limitsPointer);
            }

            return new WindowsJobObject(jobHandle);
        }

        public void Dispose()
        {
            CloseHandle(jobHandle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimits
        {
            public JobObjectBasicLimits BasicLimits;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr jobHandle, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
