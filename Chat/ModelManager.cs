using System.Text;
using System.Text.Json.Nodes;
using Rhodes.Core.Settings;

namespace Rhodes.Core.Chat;

/// <summary>
/// One model llama-server's router knows about: its preset name from models.ini, whether it's loaded,
/// and what its launch arguments say about context size and vision.
/// </summary>
public sealed record RouterModel(string Id, string Status, string? FilePath, int? ContextTokens, bool HasVision, IReadOnlyList<string> Arguments);

public static class RouterModelStates
{
    public const string Loaded = "loaded";
    public const string Loading = "loading";
    public const string Unloaded = "unloaded";
    public const string Sleeping = "sleeping";
    public const string Downloading = "downloading";
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
/// Knows which model Closure uses and switches it through llama-server's router mode (llama-server started with
/// --models-preset models.ini, one [section] per model). Every request names CurrentModel, so a switch takes effect
/// on the next message; the router unloads the old model itself because Start-Rhodes.sh runs it with --models-max 1.
/// The choice is saved as LmStudio:Model in Data/appsettings.Local.json. Without router mode (a plain llama-server
/// started with -m) the list is empty and everything works as before.
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
    private readonly LocalSettingsFile localSettingsFile;
    private readonly ILogger<ModelManager> logger;
    private readonly object stateLock = new();

    private IReadOnlyList<RouterModel> knownModels = [];
    private string currentModel;
    private Task? loadTask;
    private DateTimeOffset loadRequestedAt = DateTimeOffset.MinValue;

    public ModelManager(string baseUrl, string configuredModel, int fallbackContextTokens, string? visionSetting, LocalSettingsFile localSettingsFile, ILogger<ModelManager> logger)
    {
        // The router's /models endpoints live at the server root, not under /v1.
        var serverRoot = baseUrl.TrimEnd('/');
        if (serverRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            serverRoot = serverRoot[..^3];
        }

        routerClient = new HttpClient
        {
            BaseAddress = new Uri(serverRoot + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        currentModel = configuredModel;
        FallbackContextTokens = fallbackContextTokens;
        VisionMode = VisionModes.Parse(visionSetting);
        this.localSettingsFile = localSettingsFile;
        this.logger = logger;
    }

    /// <summary>
    /// The preset name sent as "model" with every request. Empty until the router lists its models
    /// (then the loaded one, or the first preset, is used) or when llama-server runs without router mode.
    /// </summary>
    public string CurrentModel
    {
        get
        {
            lock (stateLock)
            {
                return currentModel;
            }
        }
    }

    /// <summary>
    /// True once /models answered with router-style entries; false for a plain single-model llama-server.
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
    /// The context size Closure plans with: the ctx-size from the current model's preset when it sets one,
    /// otherwise Chat:ContextTokens. Has to match what llama-server really allocates, or compaction starts too late.
    /// </summary>
    public int ContextTokens => CurrentEntry?.ContextTokens ?? FallbackContextTokens;

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
    /// </summary>
    public async Task<IReadOnlyList<RouterModel>?> RefreshAsync(CancellationToken cancellationToken)
    {
        JsonNode? responseJson;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ListTimeout);
            using var response = await routerClient.GetAsync("models", timeoutSource.Token);
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
        if (modelId == CurrentModel)
        {
            AutoLoadPaused = true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(1));
        using var response = await routerClient.PostAsync("models/unload", CreateModelBody(modelId), timeoutSource.Token);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Unloaded {Model}", modelId);
            return null;
        }

        return $"llama-server refused to unload it ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}";
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
            using var response = await routerClient.PostAsync("models/load", CreateModelBody(modelId), timeoutSource.Token);
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
