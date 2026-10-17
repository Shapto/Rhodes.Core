using System.Net;

namespace Rhodes.Core.Chat;

public static class ModelStates
{
    public const string Online = "online";
    public const string Loading = "loading";
    public const string Offline = "offline";
}

public sealed record ModelStatusSnapshot(string State, string Detail, double? TokensPerSecond, DateTimeOffset CheckedAt)
{
    /// <summary>
    /// The error a message gets when the model isn't online.
    /// </summary>
    public string DescribeProblem()
    {
        return State == ModelStates.Loading ? "Closure's model is still loading. Try again in a moment." : $"Closure's model server isn't reachable ({Detail}).";
    }
}

/// <summary>
/// Asks llama-server's /health every few seconds, so pages can show whether Closure's model is up
/// and a message fails right away instead of hanging when it isn't.
/// </summary>
public sealed class ModelStatusMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient healthClient;
    private readonly LmStudioClient lmStudioClient;
    private readonly ILogger<ModelStatusMonitor> logger;
    private readonly SemaphoreSlim checkLock = new(1, 1);
    private ModelStatusSnapshot currentStatus = new(ModelStates.Offline, "not checked yet", null, DateTimeOffset.MinValue);

    public ModelStatusMonitor(string baseUrl, LmStudioClient lmStudioClient, ILogger<ModelStatusMonitor> logger)
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
        this.logger = logger;
    }

    public ModelStatusSnapshot Current => currentStatus with { TokensPerSecond = lmStudioClient.LastTokensPerSecond };

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
            var (state, detail) = await QueryHealthAsync(cancellationToken);
            if (state != currentStatus.State)
            {
                logger.LogInformation("Model server is now {State} ({Detail})", state, detail);
            }

            currentStatus = new ModelStatusSnapshot(state, detail, null, DateTimeOffset.Now);
        }
        finally
        {
            checkLock.Release();
        }
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
