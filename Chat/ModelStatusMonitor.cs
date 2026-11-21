using System.Net;

namespace Rhodes.Core.Chat;

public static class ModelStates
{
    public const string Online = "online";
    public const string Loading = "loading";
    public const string Offline = "offline";
}

public sealed record ModelStatusSnapshot(string State, string Detail, double? TokensPerSecond, DateTimeOffset CheckedAt, string? Model = null)
{
    /// <summary>
    /// The error a message gets when the model isn't online.
    /// </summary>
    public string DescribeProblem()
    {
        return State == ModelStates.Loading ? "Closure's model is still loading. Try again in a moment." : $"Closure's model isn't available ({Detail}).";
    }
}

/// <summary>
/// Checks llama-server every few seconds, so pages can show whether Closure's model is up
/// and a message fails right away instead of hanging when it isn't. In router mode it reads the current model's
/// status from /models (and loads it when it isn't loaded); a plain llama-server is asked /health instead.
/// </summary>
public sealed class ModelStatusMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient healthClient;
    private readonly LmStudioClient lmStudioClient;
    private readonly ModelManager modelManager;
    private readonly ILogger<ModelStatusMonitor> logger;
    private readonly SemaphoreSlim checkLock = new(1, 1);
    private ModelStatusSnapshot currentStatus = new(ModelStates.Offline, "not checked yet", null, DateTimeOffset.MinValue);

    public ModelStatusMonitor(string baseUrl, LmStudioClient lmStudioClient, ModelManager modelManager, ILogger<ModelStatusMonitor> logger)
    {
        // /health lives at the server root, not under /v1.
        var serverRoot = baseUrl.TrimEnd('/');
        if (serverRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            serverRoot = serverRoot[..^3];
        }

        healthClient = new HttpClient
        {
            BaseAddress = new Uri(serverRoot + "/"),
            Timeout = HealthTimeout,
        };
        this.lmStudioClient = lmStudioClient;
        this.modelManager = modelManager;
        this.logger = logger;
    }

    public ModelStatusSnapshot Current => currentStatus with { TokensPerSecond = lmStudioClient.LastTokensPerSecond, Model = modelManager.CurrentModel };

    /// <summary>
    /// Returns the status, checking again first if the last check didn't find the model online,
    /// so a server that just came back up doesn't have to wait for the next poll.
    /// </summary>
    public async Task<ModelStatusSnapshot> GetFreshStatusAsync(CancellationToken cancellationToken)
    {
        if (currentStatus.State != ModelStates.Online)
        {
            await CheckAsync(cancellationToken);
        }

        return Current;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            await CheckAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        await checkLock.WaitAsync(cancellationToken);
        try
        {
            var routerModels = await modelManager.RefreshAsync(cancellationToken);
            var (state, detail) = routerModels is { Count: > 0 } ? CheckRouterModel() : await QueryHealthAsync(cancellationToken);
            if (state != currentStatus.State)
            {
                logger.LogInformation("Model server is now {State} ({Detail})", state, detail);
            }

            currentStatus = new ModelStatusSnapshot(state, detail, null, DateTimeOffset.Now, modelManager.CurrentModel);
        }
        finally
        {
            checkLock.Release();
        }
    }

    /// <summary>
    /// Reads the current model's state from the router's list, and starts loading it when it's unloaded
    /// (at startup, or after the router unloaded it), unless Shapto unloaded it on purpose or the last load failed.
    /// </summary>
    private (string State, string Detail) CheckRouterModel()
    {
        var currentEntry = modelManager.CurrentEntry;
        if (currentEntry is null)
        {
            return (ModelStates.Offline, $"model \"{modelManager.CurrentModel}\" isn't in models.ini");
        }

        switch (currentEntry.Status)
        {
            case RouterModelStates.Loaded:
            case RouterModelStates.Sleeping:
                return (ModelStates.Online, "ready · " + currentEntry.Id);
            case RouterModelStates.Loading:
            case RouterModelStates.Downloading:
                return (ModelStates.Loading, currentEntry.Status + " " + currentEntry.Id);
        }

        if (modelManager.AutoLoadPaused)
        {
            return (ModelStates.Offline, modelManager.LastLoadError is { } loadError ? $"{currentEntry.Id} failed to load: {loadError}" : $"{currentEntry.Id} is unloaded · press LOAD on the home page");
        }

        if (modelManager.IsLoadInProgress)
        {
            return (ModelStates.Loading, "loading " + currentEntry.Id);
        }

        // Unloaded with no load running: either nothing asked yet (start it) or a load just ended without the model coming up.
        if (currentStatus.State == ModelStates.Loading && currentStatus.Model == currentEntry.Id)
        {
            modelManager.MarkLoadFailed("it stopped while loading (out of memory?) · see Logs/llama-server.log");
            return (ModelStates.Offline, $"{currentEntry.Id} failed to load · see Logs/llama-server.log");
        }

        modelManager.StartLoad(currentEntry.Id);
        return (ModelStates.Loading, "loading " + currentEntry.Id);
    }

    private async Task<(string State, string Detail)> QueryHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await healthClient.GetAsync("health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (ModelStates.Online, "ready");
            }

            return response.StatusCode == HttpStatusCode.ServiceUnavailable ? (ModelStates.Loading, "loading the model") : (ModelStates.Offline, $"health check returned {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (ModelStates.Offline, $"no answer within {HealthTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException exception)
        {
            return (ModelStates.Offline, exception.Message);
        }
    }
}
