using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhodes.Core.Settings;

namespace Rhodes.Core.Engines;

public static class EngineStates
{
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Calibrating = "calibrating";
    public const string Failed = "failed";
}

/// <summary>
/// Owns the model server Closure talks to: which engine is active (llama.cpp or RustingStrata), starting and stopping
/// its server, and RustingStrata's model choice and calibration. Only one engine runs at a time, since both want the
/// whole GPU. A server that was already running when the core started (Start-Rhodes.sh's llama-server) is adopted,
/// not restarted. The choices are saved as Engine:Current and Engine:StrataModel in Data/appsettings.Local.json.
/// </summary>
public sealed class EngineManager : IHostedService
{
    public const string EngineSettingKey = "Engine:Current";
    public const string StrataModelSettingKey = "Engine:StrataModel";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    // rustingstrata calibrate stops on its own after 30 minutes by default; this only catches a hung server.
    private static readonly TimeSpan CalibrationTimeout = TimeSpan.FromHours(3);

    private readonly EngineCatalog engineCatalog;
    private readonly LocalSettingsFile localSettingsFile;
    private readonly ILogger<EngineManager> logger;
    private readonly HttpClient probeClient = new() { Timeout = ProbeTimeout };
    private readonly HttpClient calibrationClient = new() { Timeout = CalibrationTimeout };
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object stateLock = new();

    private string currentEngineName;
    private string currentStrataModelId;
    private Process? ownedProcess;
    private string? ownedProcessEngineName;
    private string state = EngineStates.Starting;
    private string detail = "checking the engine";
    private string? lastError;
    private int? servedContextTokens;
    private DateTimeOffset? calibrationStartedAt;
    private string? lastCalibrationResult;
    private string? runningStrataModelId;

    public EngineManager(EngineCatalog engineCatalog, string? savedEngineName, string? savedStrataModelId, LocalSettingsFile localSettingsFile, ILogger<EngineManager> logger)
    {
        this.engineCatalog = engineCatalog;
        this.localSettingsFile = localSettingsFile;
        this.logger = logger;
        currentEngineName = savedEngineName ?? "";
        currentStrataModelId = savedStrataModelId ?? "";
    }

    public EngineCatalogSnapshot Catalog => engineCatalog.Load();

    public string EnginesFilePath => engineCatalog.EnginesFilePath;

    /// <summary>
    /// The active engine: the saved choice, or the first one in engines.ini. Null only when engines.ini has none.
    /// </summary>
    public EngineDefinition? CurrentEngine
    {
        get
        {
            var engines = Catalog.Engines;
            return engines.FirstOrDefault(engine => engine.Name == currentEngineName) ?? engines.FirstOrDefault();
        }
    }

    public bool IsStrata => CurrentEngine?.Kind == EngineKinds.RustingStrata;

    /// <summary>
    /// The OpenAI base URL of the active engine, ending in /v1.
    /// </summary>
    public string BaseUrl => (CurrentEngine?.BaseUrl ?? "http://127.0.0.1:1234/v1").TrimEnd('/');

    public string ServerRoot => CurrentEngine?.ServerRoot ?? "http://127.0.0.1:1234";

    public string ThinkingStyle => CurrentEngine?.ThinkingStyle ?? ThinkingStyles.LlamaCpp;

    /// <summary>
    /// RustingStrata's chosen model: the saved choice, or the first entry of its models file.
    /// </summary>
    public StrataModel? CurrentStrataModel
    {
        get
        {
            var strataModels = Catalog.StrataModels;
            return strataModels.FirstOrDefault(model => model.Id == currentStrataModelId) ?? strataModels.FirstOrDefault();
        }
    }

    /// <summary>
    /// The model RustingStrata was last started with (it can differ from the choice until the restart finishes).
    /// </summary>
    public string? RunningStrataModelId => runningStrataModelId;

    public string State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public string Detail
    {
        get
        {
            lock (stateLock)
            {
                return detail;
            }
        }
    }

    public string? LastError => lastError;

    public bool IsBusy => State is EngineStates.Starting or EngineStates.Stopping or EngineStates.Calibrating;

    /// <summary>
    /// The context size the running server reported in /props (RustingStrata), or null.
    /// </summary>
    public int? ServedContextTokens => servedContextTokens;

    public DateTimeOffset? CalibrationStartedAt => calibrationStartedAt;

    public string? LastCalibrationResult => lastCalibrationResult;

    /// <summary>
    /// The newest line of the active engine's log, for showing progress while it starts or calibrates.
    /// </summary>
    public string? LastLogLine => CurrentEngine is { } engine ? ReadLastLogLine(engine.LogPath) : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs in the background so the core answers right away; pages show the engine as starting meanwhile.
        _ = RunOperationAsync(BringUpCurrentEngineAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The server keeps running when the core stops or restarts, like a server Start-Rhodes.sh started.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Makes engineName the active engine: stops the other engine's server and starts this one.
    /// Returns an error text, or null when the switch is under way.
    /// </summary>
    public string? RequestSwitch(string engineName)
    {
        var targetEngine = Catalog.Engines.FirstOrDefault(engine => engine.Name == engineName);
        if (targetEngine is null)
        {
            return $"{EngineCatalog.EnginesFileName} has no engine called \"{engineName}\".";
        }

        if (!targetEngine.CanStart)
        {
            return $"[{engineName}] in {EngineCatalog.EnginesFileName} has no command, so Rhodes can't start it.";
        }

        if (IsBusy)
        {
            return $"The engine is busy ({State}). Wait until it's done.";
        }

        currentEngineName = engineName;
        localSettingsFile.Write(EngineSettingKey, JsonValue.Create(engineName));
        logger.LogInformation("Closure's engine is now {Engine}", engineName);
        _ = RunOperationAsync(async () =>
        {
            await StopOtherEnginesAsync(targetEngine);
            await StartEngineAsync(targetEngine);
        });
        return null;
    }

    /// <summary>
    /// Chooses which model RustingStrata runs and restarts it with that model when it's the active engine.
    /// </summary>
    public string? RequestStrataModel(string modelId)
    {
        var strataModel = Catalog.StrataModels.FirstOrDefault(model => model.Id == modelId);
        if (strataModel is null)
        {
            return $"RustingStrata's models file has no model called \"{modelId}\".";
        }

        if (!strataModel.FileExists)
        {
            return $"{strataModel.ModelPath} doesn't exist. Check model = and models-folder =.";
        }

        if (IsBusy)
        {
            return $"The engine is busy ({State}). Wait until it's done.";
        }

        currentStrataModelId = modelId;
        localSettingsFile.Write(StrataModelSettingKey, JsonValue.Create(modelId));
        logger.LogInformation("RustingStrata's model is now {Model}", modelId);
        if (IsStrata && CurrentEngine is { } strataEngine)
        {
            _ = RunOperationAsync(async () =>
            {
                await StopEngineAsync(strataEngine);
                await StartEngineAsync(strataEngine);
            });
        }

        return null;
    }

    public string? RequestStart()
    {
        if (CurrentEngine is not { } engine)
        {
            return $"{EngineCatalog.EnginesFileName} has no engines.";
        }

        if (IsBusy)
        {
            return $"The engine is busy ({State}). Wait until it's done.";
        }

        _ = RunOperationAsync(async () =>
        {
            await StopOtherEnginesAsync(engine);
            await StartEngineAsync(engine);
        });
        return null;
    }

    public string? RequestStop()
    {
        if (CurrentEngine is not { } engine)
        {
            return $"{EngineCatalog.EnginesFileName} has no engines.";
        }

        if (State is EngineStates.Calibrating)
        {
            return "RustingStrata is calibrating. Wait until it's done.";
        }

        _ = RunOperationAsync(() => StopEngineAsync(engine));
        return null;
    }

    /// <summary>
    /// Calibrates RustingStrata's current model on this machine (the chat page's "calibrate"): the server measures
    /// the fastest prefill chunk and draft settings, saves them for every later run, and loads the model with them.
    /// Starts RustingStrata first when it isn't running.
    /// </summary>
    public string? RequestCalibration()
    {
        if (!IsStrata || CurrentEngine is not { } strataEngine)
        {
            return "Calibration is for RustingStrata. Switch the engine to it first.";
        }

        if (CurrentStrataModel is not { } strataModel)
        {
            return "RustingStrata has no models yet. Add one to its models file.";
        }

        if (IsBusy)
        {
            return $"The engine is busy ({State}). Wait until it's done.";
        }

        _ = RunOperationAsync(async () =>
        {
            if (runningStrataModelId != strataModel.Id || await ProbeHealthAsync(strataEngine, CancellationToken.None) != HttpStatusCode.OK)
            {
                await StopOtherEnginesAsync(strataEngine);
                await StopEngineAsync(strataEngine);
                await StartEngineAsync(strataEngine);
            }

            await CalibrateAsync(strataEngine, strataModel);
        });
        return null;
    }

    /// <summary>
    /// Called by the status check when the running engine's server stopped answering (it crashed, or was closed
    /// from outside): the panel then shows it as failed with a START button instead of still running.
    /// </summary>
    public void ReportUnreachable(string reason)
    {
        if (operationLock.CurrentCount == 0 || State != EngineStates.Running)
        {
            return;
        }

        if (ownedProcess is { HasExited: true })
        {
            ownedProcess = null;
            ownedProcessEngineName = null;
        }

        runningStrataModelId = null;
        lastError = "the server stopped answering (" + reason + ")" + (LastLogLine is { } logLine ? " · " + logLine : "");
        SetState(EngineStates.Failed, lastError);
    }

    /// <summary>
    /// Whether RustingStrata has a calibration file for this model on this machine
    /// (~/.cache/rusting_strata/strata-&lt;file name&gt;[-backend][-devices].json).
    /// </summary>
    public static bool IsCalibrated(StrataModel strataModel)
    {
        var cacheFolder = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdgCache
            ? Path.Combine(xdgCache, "rusting_strata")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rusting_strata")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "rusting_strata");
        if (!Directory.Exists(cacheFolder))
        {
            return false;
        }

        // Named after the GGUF file, not the alias: RustingStrata keeps calibrations per file.
        var modelFileName = Path.GetFileNameWithoutExtension(strataModel.ModelPath);
        var namePattern = new Regex("^strata-" + Regex.Escape(modelFileName) + @"(-(vulkan|hip|cuda))?(-dev[0-9-]+)?\.json$", RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(cacheFolder, "strata-*.json").Any(filePath => namePattern.IsMatch(Path.GetFileName(filePath)));
    }

    private async Task BringUpCurrentEngineAsync()
    {
        if (CurrentEngine is not { } engine)
        {
            SetState(EngineStates.Failed, "no engines in " + EngineCatalog.EnginesFileName);
            return;
        }

        var healthStatus = await ProbeHealthAsync(engine, CancellationToken.None);
        if (healthStatus is not null)
        {
            // Something already answers on the active engine's address (Start-Rhodes.sh started it): adopt it.
            logger.LogInformation("{Engine} is already running at {Url}; using it as it is", engine.Name, engine.BaseUrl);
            if (engine.Kind == EngineKinds.RustingStrata)
            {
                runningStrataModelId = CurrentStrataModel?.Id;
                await ReadServedContextAsync(engine);
            }

            SetState(EngineStates.Running, healthStatus == HttpStatusCode.OK ? "already running" : "already running · loading its model");
            return;
        }

        if (!engine.CanStart)
        {
            SetState(EngineStates.Stopped, $"not running, and [{engine.Name}] has no command to start it with");
            return;
        }

        await StopOtherEnginesAsync(engine);
        await StartEngineAsync(engine);
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        await operationLock.WaitAsync();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            SetState(EngineStates.Failed, exception.Message);
            logger.LogWarning(exception, "Engine operation failed");
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task StopOtherEnginesAsync(EngineDefinition keptEngine)
    {
        foreach (var otherEngine in Catalog.Engines.Where(engine => engine.Name != keptEngine.Name))
        {
            await StopEngineAsync(otherEngine);
        }
    }

    /// <summary>
    /// Stops an engine's server: the process Rhodes started, and when its address still answers after that,
    /// any process with the engine's program name (a server Start-Rhodes.sh or a terminal started).
    /// </summary>
    private async Task StopEngineAsync(EngineDefinition engine)
    {
        var isActive = engine.Name == CurrentEngine?.Name;
        var ownedForEngine = ownedProcessEngineName == engine.Name ? ownedProcess : null;
        if (ownedForEngine is null && await ProbeHealthAsync(engine, CancellationToken.None) is null)
        {
            if (isActive)
            {
                SetState(EngineStates.Stopped, "stopped");
            }

            return;
        }

        if (isActive)
        {
            SetState(EngineStates.Stopping, "stopping " + engine.Name);
        }

        logger.LogInformation("Stopping {Engine}", engine.Name);
        if (ownedForEngine is not null)
        {
            await StopGracefullyAsync(ownedForEngine);
            ownedProcess = null;
            ownedProcessEngineName = null;
        }

        if (await ProbeHealthAsync(engine, CancellationToken.None) is not null)
        {
            foreach (var strayProcess in Process.GetProcessesByName(engine.ProcessName))
            {
                logger.LogInformation("Stopping {ProcessName} (process {ProcessId}), which Rhodes didn't start", engine.ProcessName, strayProcess.Id);
                await StopGracefullyAsync(strayProcess);
            }
        }

        // Give the port a moment to close before the next server binds the GPU and its own port.
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StopTimeout && await ProbeHealthAsync(engine, CancellationToken.None) is not null)
        {
            await Task.Delay(500);
        }

        if (engine.Kind == EngineKinds.RustingStrata)
        {
            runningStrataModelId = null;
            servedContextTokens = null;
        }

        if (isActive)
        {
            SetState(EngineStates.Stopped, "stopped");
        }
    }

    private async Task StartEngineAsync(EngineDefinition engine)
    {
        lastError = null;
        StrataModel? strataModel = null;
        string commandLine;
        // ROOT lets engines.ini commands say $ROOT for the RhodesIsland folder, like models.ini does.
        var environment = new Dictionary<string, string>(engine.Environment) { ["ROOT"] = engineCatalog.RhodesFolder };
        if (engine.Kind == EngineKinds.RustingStrata)
        {
            strataModel = CurrentStrataModel ?? throw new InvalidOperationException("RustingStrata has no models yet. Add one to " + Path.GetFileName(engine.ModelsFile));
            if (!strataModel.FileExists)
            {
                throw new InvalidOperationException($"{strataModel.ModelPath} doesn't exist. Check model = and models-folder =.");
            }

            var arguments = new List<string> { engine.Executable, "serve", "--model", strataModel.ModelPath, "--addr", engine.ListenAddress };
            arguments.AddRange(EngineCatalog.SplitArguments(engine.ServerArguments));
            arguments.AddRange(EngineCatalog.ToFlags(strataModel.Arguments));
            commandLine = string.Join(' ', arguments.Select(QuoteArgument));
            environment["STRATA_NO_UPDATE_CHECK"] = "1";
            if (!environment.ContainsKey("STRATA_NVRTC") && FindNvrtcLibrary(engine.NvrtcFolder) is { } nvrtcLibrary)
            {
                // RustingStrata loads NVRTC at run time; the pip package keeps it deep inside the venv.
                var nvrtcFolder = Path.GetDirectoryName(nvrtcLibrary)!;
                environment["STRATA_NVRTC"] = nvrtcLibrary;
                var existingLibraryPath = environment.GetValueOrDefault("LD_LIBRARY_PATH") ?? System.Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLibraryPath) ? nvrtcFolder : nvrtcFolder + ":" + existingLibraryPath;
            }
            foreach (var (variableName, variableValue) in strataModel.Environment)
            {
                environment[variableName] = variableValue;
            }
        }
        else
        {
            commandLine = engine.Command?.Trim() is { Length: > 0 } command ? command : throw new InvalidOperationException($"[{engine.Name}] in {EngineCatalog.EnginesFileName} has no command.");
        }

        SetState(EngineStates.Starting, strataModel is null ? "starting " + engine.Name : $"starting {engine.Name} · {strataModel.Id}");
        Directory.CreateDirectory(Path.GetDirectoryName(engine.LogPath)!);
        await File.AppendAllTextAsync(engine.LogPath, $"{Environment.NewLine}===== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} started by Rhodes: {commandLine}{Environment.NewLine}");
        logger.LogInformation("Starting {Engine}: {CommandLine}", engine.Name, commandLine);

        var serverProcess = Process.Start(CreateStartInfo(commandLine, engine, environment)) ?? throw new InvalidOperationException("the server process didn't start");
        ownedProcess = serverProcess;
        ownedProcessEngineName = engine.Name;
        runningStrataModelId = strataModel?.Id;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartTimeout)
        {
            await Task.Delay(1000);
            if (serverProcess.HasExited)
            {
                ownedProcess = null;
                runningStrataModelId = null;
                throw new InvalidOperationException($"{engine.Name} exited with code {serverProcess.ExitCode} · {ReadLastLogLine(engine.LogPath) ?? "see " + engine.LogPath}");
            }

            var healthStatus = await ProbeHealthAsync(engine, CancellationToken.None);
            if (healthStatus == HttpStatusCode.OK)
            {
                if (engine.Kind == EngineKinds.RustingStrata)
                {
                    await ReadServedContextAsync(engine);
                }

                SetState(EngineStates.Running, strataModel is null ? "running" : "running · " + strataModel.Id);
                return;
            }

            SetState(EngineStates.Starting, (strataModel is null ? "starting" : "loading " + strataModel.Id) + (ReadLastLogLine(engine.LogPath) is { } logLine ? " · " + logLine : ""));
        }

        throw new InvalidOperationException($"{engine.Name} didn't come up within {StartTimeout.TotalMinutes:0} minutes · see {engine.LogPath}");
    }

    private async Task CalibrateAsync(EngineDefinition strataEngine, StrataModel strataModel)
    {
        calibrationStartedAt = DateTimeOffset.Now;
        SetState(EngineStates.Calibrating, "calibrating " + strataModel.Id);
        logger.LogInformation("Calibrating {Model} on RustingStrata", strataModel.Id);
        try
        {
            var requestBody = new JsonObject { ["path"] = strataModel.ModelPath, ["calibrate"] = true };
            using var response = await calibrationClient.PostAsync(strataEngine.ServerRoot + "/api/load", new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                lastCalibrationResult = $"failed at {DateTimeOffset.Now:HH:mm} ({(int)response.StatusCode})";
                throw new InvalidOperationException($"calibration failed ({(int)response.StatusCode}): {errorText}");
            }

            lastCalibrationResult = $"done at {DateTimeOffset.Now:HH:mm}, took {(DateTimeOffset.Now - calibrationStartedAt.Value).TotalMinutes:0} min";
            logger.LogInformation("Calibrated {Model}", strataModel.Id);
            await ReadServedContextAsync(strataEngine);
            SetState(EngineStates.Running, "running · " + strataModel.Id + " (calibrated)");
        }
        finally
        {
            calibrationStartedAt = null;
        }
    }

    /// <summary>
    /// 200 when the server is up with a model, 503 while it loads, another code for anything else that answers,
    /// and null when nothing answers. Servers without /health count as up when /v1/models answers.
    /// </summary>
    private async Task<HttpStatusCode?> ProbeHealthAsync(EngineDefinition engine, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await probeClient.GetAsync(engine.ServerRoot + "/health", cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return response.StatusCode;
            }

            using var modelsResponse = await probeClient.GetAsync(engine.BaseUrl.TrimEnd('/') + "/models", cancellationToken);
            return modelsResponse.IsSuccessStatusCode ? HttpStatusCode.OK : modelsResponse.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task ReadServedContextAsync(EngineDefinition engine)
    {
        try
        {
            using var response = await probeClient.GetAsync(engine.ServerRoot + "/props");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var propsJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            servedContextTokens = propsJson?["default_generation_settings"]?["n_ctx"]?.GetValue<int>() is > 0 and var contextTokens ? contextTokens : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            servedContextTokens = null;
        }
    }

    private ProcessStartInfo CreateStartInfo(string commandLine, EngineDefinition engine, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Directory.Exists(engine.WorkingFolder) ? engine.WorkingFolder : engineCatalog.RhodesFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The shell sends the server's output straight to its log file, so the server doesn't depend on the core
        // reading a pipe and keeps running when the core restarts.
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{commandLine} >> \"{engine.LogPath}\" 2>&1\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            // RustingStrata's line is one program, so the shell becomes it. llama.cpp's command can be several steps
            // (sed && llama-server), so it runs as a group with the log redirect around the whole thing.
            startInfo.ArgumentList.Add(engine.Kind == EngineKinds.RustingStrata
                ? $"exec {commandLine} >> {QuoteArgument(engine.LogPath)} 2>&1"
                : $"{{ {commandLine}\n}} >> {QuoteArgument(engine.LogPath)} 2>&1");
        }

        foreach (var (variableName, variableValue) in environment)
        {
            startInfo.Environment[variableName] = variableValue;
        }

        return startInfo;
    }

    /// <summary>
    /// Asks a server to quit like Ctrl+C does (SIGINT, to it and its children), and kills it only if it's still there
    /// after the grace time. RustingStrata saves its expert routing profile on a clean exit, so the next start is
    /// fast from the first token; a kill would lose that.
    /// </summary>
    private static async Task StopGracefullyAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                await SendInterruptAsync(process.Id);
                using var graceSource = new CancellationTokenSource(StopTimeout);
                try
                {
                    await process.WaitForExitAsync(graceSource.Token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Didn't quit in time: kill it below.
                }
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await KillAsync(process);
    }

    private static async Task SendInterruptAsync(int processId)
    {
        // Children first (llama.cpp's command runs under a shell), then the process itself.
        foreach (var (programName, programArguments) in new[] { ("pkill", $"-INT -P {processId}"), ("kill", $"-INT {processId}") })
        {
            try
            {
                using var signalProcess = Process.Start(new ProcessStartInfo(programName, programArguments) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
                if (signalProcess is not null)
                {
                    await signalProcess.WaitForExitAsync();
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // pkill or kill missing: the kill after the grace time still stops it.
            }
        }
    }

    /// <summary>
    /// The NVRTC library in the folder (searched through subfolders, like a pip venv's site-packages), preferring
    /// the versioned file with the highest major version. Null when there's none.
    /// </summary>
    private static string? FindNvrtcLibrary(string? nvrtcFolder)
    {
        if (nvrtcFolder is null || !Directory.Exists(nvrtcFolder))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(nvrtcFolder, "libnvrtc.so*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None })
                .Where(filePath => !Path.GetFileName(filePath).Contains("builtins", StringComparison.Ordinal))
                .OrderByDescending(filePath => Regex.Match(Path.GetFileName(filePath), @"\.so\.(\d+)") is { Success: true } versionMatch ? int.Parse(versionMatch.Groups[1].Value) : 0)
                .ThenBy(filePath => filePath.Length)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var waitSource = new CancellationTokenSource(StopTimeout);
                await process.WaitForExitAsync(waitSource.Token);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Already gone, or not ours to stop; the port check after this says whether it worked.
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (OperatingSystem.IsWindows())
        {
            return argument.Contains(' ') || argument.Contains('"') ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;
        }

        return argument.Length > 0 && argument.All(character => char.IsLetterOrDigit(character) || "-_./:=,+@%".Contains(character))
            ? argument
            : "'" + argument.Replace("'", "'\\''") + "'";
    }

    private static string? ReadLastLogLine(string logPath)
    {
        try
        {
            using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var readLength = (int)Math.Min(logStream.Length, 4096);
            logStream.Seek(-readLength, SeekOrigin.End);
            var tailBuffer = new byte[readLength];
            logStream.ReadExactly(tailBuffer);
            // Progress lines often end in \r instead of \n, so both count as line ends.
            var lastLine = Encoding.UTF8.GetString(tailBuffer)
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (lastLine is null)
            {
                return null;
            }

            var cleanLine = StripTerminalCodes(lastLine);
            return cleanLine.Length > 160 ? cleanLine[..160] + "…" : cleanLine;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StripTerminalCodes(string logLine)
    {
        return Regex.Replace(logLine, @"\x1B\[[0-9;]*[A-Za-z]", "");
    }

    private void SetState(string newState, string newDetail)
    {
        lock (stateLock)
        {
            if (newState != state)
            {
                logger.LogInformation("Engine is now {State} ({Detail})", newState, newDetail);
            }

            state = newState;
            detail = newDetail;
        }
    }
}

public sealed record EngineChoice(string Engine);
