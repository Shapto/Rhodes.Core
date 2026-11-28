using System.Text;

namespace Rhodes.Core.Engines;

public static class EngineKinds
{
    public const string LlamaCpp = "llama.cpp";
    public const string RustingStrata = "rustingstrata";

    public static string? Parse(string? kindText)
    {
        return kindText?.Trim().ToLowerInvariant() switch
        {
            "llama.cpp" or "llamacpp" or "llama-server" => LlamaCpp,
            "rustingstrata" or "strata" or "rusting-strata" => RustingStrata,
            _ => null,
        };
    }
}

/// <summary>
/// Which request fields carry the thinking settings. llama-server reads reasoning_budget_tokens (and the message it
/// appends when the budget runs out); RustingStrata reads thinking_budget. Both read chat_template_kwargs.enable_thinking.
/// </summary>
public static class ThinkingStyles
{
    public const string LlamaCpp = "llama.cpp";
    public const string RustingStrata = "rustingstrata";
}

/// <summary>
/// One model engine from engines.ini: where its server listens, how Rhodes starts it, and its own settings.
/// </summary>
public sealed record EngineDefinition(
    string Name,
    string Kind,
    string BaseUrl,
    string? Command,
    string Executable,
    string ServerArguments,
    string? ModelsFolder,
    string? ModelsFile,
    string LogPath,
    string WorkingFolder,
    int? ContextTokens,
    string ThinkingStyle,
    string ProcessName,
    IReadOnlyDictionary<string, string> Environment,
    string? NvrtcFolder)
{
    /// <summary>
    /// The server address without /v1, where /health, /props and the router's /models live.
    /// </summary>
    public string ServerRoot
    {
        get
        {
            var serverRoot = BaseUrl.TrimEnd('/');
            return serverRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? serverRoot[..^3] : serverRoot;
        }
    }

    /// <summary>
    /// host:port for RustingStrata's --addr, taken from the URL.
    /// </summary>
    public string ListenAddress
    {
        get
        {
            var serverUri = new Uri(ServerRoot);
            return $"{serverUri.Host}:{serverUri.Port}";
        }
    }

    public bool CanStart => Kind == EngineKinds.RustingStrata || !string.IsNullOrWhiteSpace(Command);
}

/// <summary>
/// One model RustingStrata can serve, from its models file: the GGUF and the flags it's started with.
/// </summary>
public sealed record StrataModel(string Id, string ModelPath, IReadOnlyList<KeyValuePair<string, string>> Arguments, IReadOnlyDictionary<string, string> Environment, int? ContextTokens, bool HasVision, IReadOnlyDictionary<string, double> Sampling)
{
    public bool FileExists => File.Exists(ModelPath);

    /// <summary>
    /// The name RustingStrata lists the loaded model under: its alias = when the entry sets one, else the file name
    /// without .gguf. Requests send this, so the server never swaps to another file because a name happens to match.
    /// </summary>
    public string ServedName => Arguments.LastOrDefault(pair => string.Equals(pair.Key, "alias", StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } alias ? alias : Path.GetFileNameWithoutExtension(ModelPath);
}

public sealed record EngineCatalogSnapshot(IReadOnlyList<EngineDefinition> Engines, IReadOnlyList<StrataModel> StrataModels, string? Problem);

/// <summary>
/// Reads Data/engines.ini (the engines) and RustingStrata's models file. Both are read again on every call,
/// so edits apply without restarting the core. Writes commented starter files the first time.
/// </summary>
public sealed class EngineCatalog
{
    public const string EnginesFileName = "engines.ini";
    public const string StrataModelsFileName = "strata-models.ini";

    // rhodes.<key> in a model's section sets that sampling value in Closure's requests (the request field it becomes).
    private static readonly Dictionary<string, string> SamplingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperature"] = "temperature",
        ["top-p"] = "top_p",
        ["top-k"] = "top_k",
        ["min-p"] = "min_p",
        ["presence-penalty"] = "presence_penalty",
        ["repeat-penalty"] = "repeat_penalty",
    };

    // Keys of a strata-models.ini section that Rhodes handles itself instead of passing on as --flags.
    private static readonly HashSet<string> ReservedModelKeys = new(StringComparer.OrdinalIgnoreCase) { "model" };

    private readonly string dataFolder;
    private readonly string rhodesFolder;

    public EngineCatalog(string dataFolder, string? legacyLlamaBaseUrl)
    {
        this.dataFolder = dataFolder;
        rhodesFolder = Path.GetDirectoryName(Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar)) ?? dataFolder;
        EnsureStarterFiles(legacyLlamaBaseUrl);
    }

    public string EnginesFilePath => Path.Combine(dataFolder, EnginesFileName);

    public string RhodesFolder => rhodesFolder;

    public EngineCatalogSnapshot Load()
    {
        var engines = new List<EngineDefinition>();
        var problems = new List<string>();
        IReadOnlyList<IniSection> engineSections;
        try
        {
            engineSections = IniFile.Read(EnginesFilePath);
        }
        catch (IOException exception)
        {
            return new EngineCatalogSnapshot([], [], $"can't read {EnginesFileName}: {exception.Message}");
        }

        foreach (var engineSection in engineSections)
        {
            var kind = EngineKinds.Parse(engineSection.Get("type"));
            if (kind is null)
            {
                problems.Add($"[{engineSection.Name}] needs type = llama.cpp or type = rustingstrata");
                continue;
            }

            var baseUrl = engineSection.Get("url") ?? (kind == EngineKinds.LlamaCpp ? "http://127.0.0.1:1234/v1" : "http://127.0.0.1:8080/v1");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                problems.Add($"[{engineSection.Name}] has a url that isn't a web address: {baseUrl}");
                continue;
            }

            var command = engineSection.Get("command");
            var executable = engineSection.Get("executable") ?? "rustingstrata";
            var processName = engineSection.Get("process-name") ?? Path.GetFileNameWithoutExtension(kind == EngineKinds.LlamaCpp ? FirstWord(command) ?? "llama-server" : executable);
            var thinkingStyle = engineSection.Get("thinking-style")?.Trim().ToLowerInvariant() switch
            {
                "llama.cpp" => ThinkingStyles.LlamaCpp,
                "rustingstrata" => ThinkingStyles.RustingStrata,
                _ => kind == EngineKinds.LlamaCpp ? ThinkingStyles.LlamaCpp : ThinkingStyles.RustingStrata,
            };
            // env.NAME = value sets an environment variable for this engine's server; $ROOT is the RhodesIsland folder.
            var engineEnvironment = engineSection.Values
                .Where(pair => pair.Key.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key[4..], pair => ExpandRoot(pair.Value));
            engines.Add(new EngineDefinition(
                engineSection.Name,
                kind,
                baseUrl,
                command,
                ExpandPath(executable, mustLookLikePath: true),
                engineSection.Get("arguments") ?? (kind == EngineKinds.RustingStrata ? "--gpu" : ""),
                engineSection.Get("models-folder") is { Length: > 0 } modelsFolder ? ResolvePath(modelsFolder) : null,
                kind == EngineKinds.RustingStrata ? ResolvePath(engineSection.Get("models") ?? Path.Combine("Data", StrataModelsFileName)) : null,
                ResolvePath(engineSection.Get("log") ?? Path.Combine("Logs", kind == EngineKinds.LlamaCpp ? "llama-server.log" : "rustingstrata.log")),
                ResolvePath(engineSection.Get("working-folder") ?? "."),
                int.TryParse(engineSection.Get("context-tokens"), out var contextTokens) && contextTokens > 0 ? contextTokens : null,
                thinkingStyle,
                processName,
                engineEnvironment,
                kind == EngineKinds.RustingStrata ? ResolvePath(engineSection.Get("nvrtc-folder") ?? "nvrtc") : null));
        }

        var strataModels = new List<StrataModel>();
        var strataEngine = engines.FirstOrDefault(engine => engine.Kind == EngineKinds.RustingStrata);
        if (strataEngine?.ModelsFile is { } modelsFile)
        {
            if (!File.Exists(modelsFile))
            {
                problems.Add($"RustingStrata's models file {modelsFile} doesn't exist");
            }
            else
            {
                foreach (var modelSection in IniFile.Read(modelsFile))
                {
                    if (modelSection.Get("model") is not { Length: > 0 } modelPath)
                    {
                        problems.Add($"[{modelSection.Name}] in {Path.GetFileName(modelsFile)} needs model = path/to/file.gguf");
                        continue;
                    }

                    var arguments = new List<KeyValuePair<string, string>>();
                    var environment = new Dictionary<string, string>();
                    var sampling = new Dictionary<string, double>();
                    foreach (var (settingKey, settingValue) in modelSection.Values)
                    {
                        if (settingKey.StartsWith("rhodes.", StringComparison.OrdinalIgnoreCase))
                        {
                            // Settings for Rhodes itself, not flags for the server: the sampling Closure's requests use.
                            if (SamplingKeys.TryGetValue(settingKey[7..], out var requestField) && double.TryParse(settingValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var samplingValue))
                            {
                                sampling[requestField] = samplingValue;
                            }
                            else
                            {
                                problems.Add($"[{modelSection.Name}] has {settingKey} = {settingValue}, which Rhodes doesn't know (sampling keys: {string.Join(", ", SamplingKeys.Keys.Select(key => "rhodes." + key))})");
                            }
                        }
                        else if (settingKey.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
                        {
                            environment[settingKey[4..]] = ExpandRoot(settingValue);
                        }
                        else if (!ReservedModelKeys.Contains(settingKey))
                        {
                            arguments.Add(new KeyValuePair<string, string>(settingKey, settingValue));
                        }
                    }

                    strataModels.Add(new StrataModel(
                        modelSection.Name,
                        ResolveModelPath(modelPath, strataEngine.ModelsFolder),
                        arguments,
                        environment,
                        int.TryParse(modelSection.Get("ctx"), out var modelContextTokens) && modelContextTokens > 0 ? modelContextTokens : null,
                        modelSection.Get("mmproj") is { Length: > 0 },
                        sampling));
                }
            }
        }

        if (engines.Count == 0 && problems.Count == 0)
        {
            problems.Add($"{EnginesFileName} has no engines");
        }

        return new EngineCatalogSnapshot(engines, strataModels, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>
    /// The extra arguments a model entry adds to rustingstrata: "ctx = 32768" becomes --ctx 32768, "gpu = true"
    /// becomes --gpu and "something = false" is left out. Paths in values that point at files are resolved.
    /// </summary>
    public static IEnumerable<string> ToFlags(IEnumerable<KeyValuePair<string, string>> settings)
    {
        foreach (var (settingKey, settingValue) in settings)
        {
            var flagName = "--" + settingKey.TrimStart('-');
            if (string.Equals(settingValue, "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return flagName;
            if (settingValue.Length > 0 && !string.Equals(settingValue, "true", StringComparison.OrdinalIgnoreCase))
            {
                yield return settingValue;
            }
        }
    }

    /// <summary>
    /// Splits a line of arguments the way a shell would for plain words and "quoted" or 'quoted' parts.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string argumentLine)
    {
        var arguments = new List<string>();
        var currentArgument = new StringBuilder();
        var hasArgument = false;
        char? openQuote = null;
        foreach (var character in argumentLine)
        {
            if (openQuote is { } quote)
            {
                if (character == quote)
                {
                    openQuote = null;
                }
                else
                {
                    currentArgument.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                openQuote = character;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (hasArgument)
                {
                    arguments.Add(currentArgument.ToString());
                    currentArgument.Clear();
                    hasArgument = false;
                }
            }
            else
            {
                currentArgument.Append(character);
                hasArgument = true;
            }
        }

        if (hasArgument)
        {
            arguments.Add(currentArgument.ToString());
        }

        return arguments;
    }

    private string ExpandRoot(string configuredValue)
    {
        return configuredValue.Replace("$ROOT", rhodesFolder, StringComparison.Ordinal);
    }

    private string ResolveModelPath(string modelPath, string? modelsFolder)
    {
        var expandedPath = ExpandHome(ExpandRoot(modelPath));
        if (Path.IsPathRooted(expandedPath))
        {
            return expandedPath;
        }

        return Path.GetFullPath(Path.Combine(modelsFolder ?? rhodesFolder, expandedPath));
    }

    private string ResolvePath(string configuredPath)
    {
        var expandedPath = ExpandHome(ExpandRoot(configuredPath));
        return Path.IsPathRooted(expandedPath) ? expandedPath : Path.GetFullPath(Path.Combine(rhodesFolder, expandedPath));
    }

    /// <summary>
    /// A bare program name ("rustingstrata") is left for PATH to find; anything with a folder in it is resolved.
    /// </summary>
    private string ExpandPath(string configuredPath, bool mustLookLikePath)
    {
        var expandedPath = ExpandHome(ExpandRoot(configuredPath));
        var looksLikePath = expandedPath.Contains('/') || expandedPath.Contains('\\');
        return mustLookLikePath && !looksLikePath ? expandedPath : ResolvePath(expandedPath);
    }

    private static string ExpandHome(string configuredPath)
    {
        if (configuredPath == "~" || configuredPath.StartsWith("~/", StringComparison.Ordinal))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + configuredPath[1..];
        }

        return configuredPath;
    }

    private static string? FirstWord(string? commandLine)
    {
        return string.IsNullOrWhiteSpace(commandLine) ? null : SplitArguments(commandLine).FirstOrDefault();
    }

    private void EnsureStarterFiles(string? legacyLlamaBaseUrl)
    {
        if (!File.Exists(EnginesFilePath))
        {
            var llamaBaseUrl = string.IsNullOrWhiteSpace(legacyLlamaBaseUrl) ? "http://127.0.0.1:1234/v1" : legacyLlamaBaseUrl.Trim();
            var llamaPort = Uri.TryCreate(llamaBaseUrl, UriKind.Absolute, out var llamaUri) ? llamaUri.Port : 1234;
            // The rustingstrata program: a release binary in RustingStrata/, a source build in its target/release, else PATH.
            var strataExecutable = new[] { "RustingStrata/rustingstrata-linux-x86_64", "RustingStrata/rustingstrata", "RustingStrata/target/release/rustingstrata" }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(rhodesFolder, candidate))) ?? "rustingstrata";
            File.WriteAllText(EnginesFilePath, $"""
                ; Model engines Rhodes can run Closure on. The home page's ENGINE panel switches between them:
                ; it stops the running engine's server and starts the other one. Edits apply on the next switch or start,
                ; no core restart needed. Relative paths start at the RhodesIsland folder; $ROOT is that folder too
                ; (in commands it's an environment variable, so the shell expands it).
                ;
                ; Keys:
                ;   type            llama.cpp or rustingstrata
                ;   url             where the server listens, ending in /v1
                ;   context-tokens  context Closure plans with when the model doesn't say (else the home page setting)
                ;   thinking-style  llama.cpp or rustingstrata: which request fields carry the thinking budget
                ;   log             file the server's output is appended to
                ;   process-name    program name used to find a copy of this server Rhodes didn't start itself
                ;   env.NAME        an environment variable for the server

                [llama.cpp]
                type = llama.cpp
                url = {llamaBaseUrl}
                ; What Start-Rhodes.sh used to do: fill $ROOT into models.ini, then start llama-server in router mode
                ; from the llama folder (its .so files sit next to it). Runs through /bin/sh.
                working-folder = llama
                command = sed "s|\$ROOT|$ROOT|g" "$ROOT/models.ini" > "$ROOT/llama/models.resolved.ini" && exec ./llama-server --models-preset "$ROOT/llama/models.resolved.ini" --models-max 1 --no-models-autoload --host 0.0.0.0 --port {llamaPort}
                env.LD_LIBRARY_PATH = $ROOT/llama
                process-name = llama-server
                log = Logs/llama-server.log

                [RustingStrata]
                type = rustingstrata
                url = http://127.0.0.1:8080/v1
                executable = {strataExecutable}
                ; Flags added to every model's command (rustingstrata serve --model … --addr … <these> <the model's own flags>).
                arguments = --gpu
                ; RustingStrata compiles its CUDA kernels with NVRTC. Rhodes looks for libnvrtc.so* in this folder
                ; (the pip venv with nvidia-cuda-nvrtc) and sets STRATA_NVRTC and LD_LIBRARY_PATH from it.
                ; Without it, RustingStrata falls back to Vulkan (slower).
                nvrtc-folder = nvrtc
                ; The models RustingStrata can run, one [section] each.
                models = Data/{StrataModelsFileName}
                ; Where relative model paths in that file point: the same Models folder models.ini uses.
                models-folder = Models
                log = Logs/rustingstrata.log
                """ + Environment.NewLine);
        }

        var strataModelsPath = Path.Combine(dataFolder, StrataModelsFileName);
        if (!File.Exists(strataModelsPath))
        {
            File.WriteAllText(strataModelsPath, """
                ; Models RustingStrata can serve. One [section] per model; the section name is what the home page shows.
                ; model = the GGUF file (relative paths start at models-folder from engines.ini, or give a full path).
                ; Every other key becomes a flag for "rustingstrata serve": ctx = 32768 → --ctx 32768, gpu = true → --gpu,
                ; something = false is left out. env.NAME = value sets an environment variable for that model only.
                ; Edits apply the next time the model is started (USE or LOAD on the home page).
                ; Always set ctx: without it RustingStrata uses 4096 for Qwen models, and for Inkling 2 the trained
                ; context (1M tokens), and Closure plans her compaction with whatever the server reports.
                ;
                ; cache-ram: RustingStrata's default (4096 MB) is too much for 16 GB of RAM, so set it.
                ; Calibrate (the home page's CALIBRATE button) runs with the same ctx and kv as the running server.
                ;
                ; [qwen3.6-35b-a3b-q3kxl]
                ; model = Qwen3.6-35B-A3B-UD-Q3_K_XL.gguf
                ; ctx = 64000
                ; kv = q8
                ; cache-ram = 1024
                ;
                ; [inkling-2-mini]
                ; model = Inkling-2-Mini-IQ4_NL.gguf
                ; ctx = 32768
                ; kv = q8
                ; cache-ram = 1024
                """ + Environment.NewLine);
        }
    }
}
