using System.Text;
using System.Text.Json.Nodes;
using Rhodes.Core.Engines;
using Rhodes.Core.Settings;

namespace Rhodes.Core.Chat;

/// <summary>
/// One model the active engine can run: for llama-server its preset name from models.ini, whether it's loaded,
/// and what its launch arguments say about context size and vision; for RustingStrata an entry of its models file
/// (Calibrated says whether RustingStrata has measured it on this machine).
/// </summary>
public sealed record RouterModel(string Id, string Status, string? FilePath, int? ContextTokens, bool HasVision, IReadOnlyList<string> Arguments, bool? Calibrated = null);

public static class RouterModelStates
{
    public const string Loaded = "loaded";
    public const string Loading = "loading";
    public const string Unloaded = "unloaded";
    public const string Sleeping = "sleeping";
    public const string Downloading = "downloading";
    public const string Calibrating = "calibrating";
}

public static class VisionModes
{
    public const string Auto = "auto";
    public const string On = "on";
    public const string Off = "off";

    public static string Parse(string? settingText)
    {
        return settingText?.Trim().ToLowerInvariant() switch
        {
            "on" or "true" => On,
            "off" or "false" => Off,
            _ => Auto,
        };
    }
}

/// <summary>
/// Knows which model Closure uses and switches it. On llama.cpp that goes through llama-server's router mode
/// (llama-server started with --models-preset models.ini, one [section] per model): every request names CurrentModel,
/// so a switch takes effect on the next message, and the router unloads the old model itself because it runs with
/// --models-max 1. The choice is saved as LmStudio:Model in Data/appsettings.Local.json. Without router mode (a plain
/// llama-server started with -m) the list is empty and everything works as before. On RustingStrata the models come
/// from its models file and a switch restarts its server with the other model (see EngineManager).
/// </summary>
public sealed class ModelManager
{
    public const string SettingKey = "LmStudio:Model";
    public const string VisionSettingKey = "LmStudio:Vision";
    public const string ContextSettingKey = "Chat:ContextTokens";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(15);

    // After a load request returns, the router may take a moment to report "loading"; until then "unloaded" isn't a failure.
    private static readonly TimeSpan LoadStartGrace = TimeSpan.FromSeconds(20);

    private readonly HttpClient routerClient;
    private readonly EngineManager engineManager;
    private readonly LocalSettingsFile localSettingsFile;
    private readonly ILogger<ModelManager> logger;
    private readonly object stateLock = new();

    private IReadOnlyList<RouterModel> knownModels = [];
    private string currentModel;
    private Task? loadTask;
    private DateTimeOffset loadRequestedAt = DateTimeOffset.MinValue;

    public ModelManager(EngineManager engineManager, string configuredModel, int fallbackContextTokens, string? visionSetting, LocalSettingsFile localSettingsFile, ILogger<ModelManager> logger)
    {
        // The address changes with the engine, so every request builds its URL from engineManager.ServerRoot
        // (the router's /models endpoints live at the server root, not under /v1).
        routerClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.engineManager = engineManager;
        currentModel = configuredModel;
        FallbackContextTokens = fallbackContextTokens;
        VisionMode = VisionModes.Parse(visionSetting);
        this.localSettingsFile = localSettingsFile;
        this.logger = logger;
    }

    /// <summary>
    /// Closure's model on the active engine. On llama.cpp the preset name, empty until the router lists its models
    /// (then the loaded one, or the first preset, is used) or when llama-server runs without router mode.
    /// On RustingStrata the [section] name from its models file.
    /// </summary>
    public string CurrentModel
    {
        get
        {
            if (engineManager.IsStrata)
            {
                return engineManager.CurrentStrataModel?.Id ?? "";
            }

            lock (stateLock)
            {
                return currentModel;
            }
        }
    }

    /// <summary>
    /// The name sent as "model" with every request: the router preset on llama.cpp, and on RustingStrata the loaded
    /// file's name, which keeps RustingStrata from swapping to another file in its folder.
    /// </summary>
    public string RequestModelName => engineManager.IsStrata ? engineManager.CurrentStrataModel?.ServedName ?? "" : CurrentModel;

    /// <summary>
    /// True once /models answered with router-style entries (or RustingStrata's models file lists models);
    /// false for a plain single-model llama-server.
    /// </summary>
    public bool IsRouter { get; private set; }

    /// <summary>
    /// Set when Shapto unloaded the model on purpose or a load failed, so the status check doesn't load it right back.
    /// LOAD on the home page clears it.
    /// </summary>
    public bool AutoLoadPaused { get; private set; }

    public string? LastLoadError { get; private set; }

    public int FallbackContextTokens { get; set; }

    public string VisionMode { get; set; }

    public IReadOnlyList<RouterModel> KnownModels
    {
        get
        {
            lock (stateLock)
            {
                return knownModels;
            }
        }
    }

    public RouterModel? CurrentEntry => KnownModels.FirstOrDefault(model => model.Id == CurrentModel);

    /// <summary>
    /// The context size Closure plans with: the ctx-size from the current model's preset (or ctx from its RustingStrata
    /// entry) when it sets one, then what RustingStrata reports in /props, then the engine's context-tokens from
    /// engines.ini, and last Chat:ContextTokens. Has to match what the server really allocates, or compaction starts too late.
    /// </summary>
    public int ContextTokens => CurrentEntry?.ContextTokens
        ?? (engineManager.IsStrata ? engineManager.ServedContextTokens : null)
        ?? engineManager.CurrentEngine?.ContextTokens
        ?? FallbackContextTokens;

    /// <summary>
    /// Whether images are sent to the model. Auto means: when the current preset loads a vision projector (mmproj).
    /// </summary>
    public bool VisionEnabled => VisionMode switch
    {
        VisionModes.On => true,
        VisionModes.Off => false,
        _ => CurrentEntry?.HasVision ?? false,
    };

    public bool IsLoadInProgress => loadTask is { IsCompleted: false } || DateTimeOffset.Now - loadRequestedAt < LoadStartGrace;

    /// <summary>
    /// Asks the router for its models. Returns null when llama-server can't be reached; an empty list means no router mode.
    /// On RustingStrata the list is its models file, with the running model's state taken from EngineManager.
    /// </summary>
    public async Task<IReadOnlyList<RouterModel>?> RefreshAsync(CancellationToken cancellationToken)
    {
        if (engineManager.IsStrata)
        {
            var strataModels = ListStrataModels();
            lock (stateLock)
            {
                knownModels = strataModels;
                IsRouter = strataModels.Count > 0;
            }

            return strataModels;
        }

        JsonNode? responseJson;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ListTimeout);
            using var response = await routerClient.GetAsync(engineManager.ServerRoot + "/models", timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                SetKnownModels([], isRouter: false);
                return [];
            }

            responseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeoutSource.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var routerModels = new List<RouterModel>();
        foreach (var modelEntry in responseJson?["data"]?.AsArray() ?? [])
        {
            // A plain llama-server also answers /models (the OpenAI list) but without a status object per model.
            if (modelEntry?["id"]?.ToString() is not { } modelId || modelEntry["status"] is not JsonObject statusObject)
            {
                continue;
            }

            var launchArguments = statusObject["args"]?.AsArray().Select(argument => argument?.ToString() ?? "").ToList() ?? [];
            routerModels.Add(new RouterModel(
                modelId,
                statusObject["value"]?.ToString() ?? RouterModelStates.Unloaded,
                modelEntry["path"]?.ToString(),
                ReadContextTokens(launchArguments),
                launchArguments.Any(argument => argument is "--mmproj" or "-mm" or "--mmproj-url" or "-mmu"),
                launchArguments));
        }

        SetKnownModels(routerModels, isRouter: routerModels.Count > 0);
        return routerModels;
    }

    /// <summary>
    /// Makes modelId Closure's model, saves the choice and starts loading it. Returns an error text, or null on success.
    /// </summary>
    public string? Select(string modelId)
    {
        if (engineManager.IsStrata)
        {
            // LOAD on the current model starts RustingStrata again; USE on another one restarts it with that model.
            var isRunningCurrent = modelId == CurrentModel && engineManager.RunningStrataModelId == modelId;
            return modelId == CurrentModel && !isRunningCurrent ? engineManager.RequestStart() : engineManager.RequestStrataModel(modelId);
        }

        if (KnownModels.All(model => model.Id != modelId))
        {
            return $"llama-server has no model called \"{modelId}\". The names are the [sections] in models.ini.";
        }

        lock (stateLock)
        {
            currentModel = modelId;
        }

        localSettingsFile.Write(SettingKey, JsonValue.Create(modelId));
        logger.LogInformation("Closure's model is now {Model}", modelId);
        AutoLoadPaused = false;
        LastLoadError = null;
        StartLoad(modelId);
        return null;
    }

    /// <summary>
    /// Starts loading modelId unless a load is already running. The router unloads the previous model first.
    /// </summary>
    public void StartLoad(string modelId)
    {
        lock (stateLock)
        {
            if (loadTask is { IsCompleted: false })
            {
                return;
            }

            loadRequestedAt = DateTimeOffset.Now;
            loadTask = Task.Run(() => LoadAsync(modelId));
        }
    }

    /// <summary>
    /// Unloads a model to free the GPU. Unloading Closure's own model pauses automatic loading until LOAD is pressed.
    /// </summary>
    public async Task<string?> UnloadAsync(string modelId, CancellationToken cancellationToken)
    {
        if (engineManager.IsStrata)
        {
            // RustingStrata serves one model per server, so unloading it stops the server.
            return engineManager.RequestStop();
        }

        if (modelId == CurrentModel)
        {
            AutoLoadPaused = true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(1));
        using var response = await routerClient.PostAsync(engineManager.ServerRoot + "/models/unload", CreateModelBody(modelId), timeoutSource.Token);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Unloaded {Model}", modelId);
            return null;
        }

        return $"llama-server refused to unload it ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}";
    }

    /// <summary>
    /// Lets the status check load the current model again after an engine switch or start, even if it was
    /// unloaded on purpose (or failed) before.
    /// </summary>
    public void ResumeAutoLoad()
    {
        AutoLoadPaused = false;
        LastLoadError = null;
    }

    /// <summary>
    /// Called by the status check when the current model shows as unloaded although no load is running:
    /// the load must have failed (llama-server's log says why), so stop retrying until Shapto presses LOAD.
    /// </summary>
    public void MarkLoadFailed(string reason)
    {
        if (AutoLoadPaused)
        {
            return;
        }

        AutoLoadPaused = true;
        LastLoadError = reason;
        logger.LogWarning("Loading {Model} failed: {Reason}", CurrentModel, reason);
    }

    private async Task LoadAsync(string modelId)
    {
        try
        {
            using var timeoutSource = new CancellationTokenSource(LoadTimeout);
            using var response = await routerClient.PostAsync(engineManager.ServerRoot + "/models/load", CreateModelBody(modelId), timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                AutoLoadPaused = true;
                LastLoadError = $"llama-server refused to load it ({(int)response.StatusCode}): {errorText}";
                logger.LogWarning("Loading {Model} failed: {Error}", modelId, LastLoadError);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AutoLoadPaused = true;
            LastLoadError = $"the load request failed: {exception.Message}";
            logger.LogWarning(exception, "Loading {Model} failed", modelId);
        }
        finally
        {
            loadRequestedAt = DateTimeOffset.Now;
        }
    }

    private IReadOnlyList<RouterModel> ListStrataModels()
    {
        var engineState = engineManager.State;
        var runningModelId = engineManager.RunningStrataModelId;
        return engineManager.Catalog.StrataModels
            .Select(strataModel => new RouterModel(
                strataModel.Id,
                strataModel.Id != runningModelId ? RouterModelStates.Unloaded : engineState switch
                {
                    EngineStates.Running => RouterModelStates.Loaded,
                    EngineStates.Starting => RouterModelStates.Loading,
                    EngineStates.Calibrating => RouterModelStates.Calibrating,
                    _ => RouterModelStates.Unloaded,
                },
                strataModel.ModelPath,
                strataModel.ContextTokens,
                strataModel.HasVision,
                EngineCatalog.ToFlags(strataModel.Arguments).ToList(),
                EngineManager.IsCalibrated(strataModel)))
            .ToList();
    }

    private void SetKnownModels(IReadOnlyList<RouterModel> routerModels, bool isRouter)
    {
        lock (stateLock)
        {
            knownModels = routerModels;
            IsRouter = isRouter;
            if (!isRouter || routerModels.Any(model => model.Id == currentModel))
            {
                return;
            }

            // No saved choice yet (or its preset was removed): take whatever is loaded, else the first preset.
            var adoptedModel = routerModels.FirstOrDefault(model => model.Status == RouterModelStates.Loaded) ?? routerModels[0];
            if (!string.IsNullOrEmpty(currentModel))
            {
                logger.LogWarning("The saved model {Model} isn't in models.ini; using {Adopted}", currentModel, adoptedModel.Id);
            }

            currentModel = adoptedModel.Id;
        }
    }

    private static StringContent CreateModelBody(string modelId)
    {
        return new StringContent(new JsonObject { ["model"] = modelId }.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static int? ReadContextTokens(IReadOnlyList<string> launchArguments)
    {
        for (var argumentIndex = 0; argumentIndex < launchArguments.Count - 1; argumentIndex++)
        {
            if (launchArguments[argumentIndex] is "-c" or "--ctx-size" or "-ctx" && int.TryParse(launchArguments[argumentIndex + 1], out var contextTokens) && contextTokens > 0)
            {
                return contextTokens;
            }
        }

        return null;
    }
}

public sealed record ModelChoice(string Model);
