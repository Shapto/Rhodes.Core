namespace Rhodes.Core.Memory;

/// <summary>
/// Backs up memory.db once a day while Rhodes runs (there is also one backup on every start).
/// </summary>
public sealed class MemoryBackupService : BackgroundService
{
    private readonly MemoryStore memoryStore;
    private readonly string backupFolder;
    private readonly int copiesToKeep;
    private readonly ILogger<MemoryBackupService> logger;

    public MemoryBackupService(MemoryStore memoryStore, IConfiguration configuration, ILogger<MemoryBackupService> logger)
    {
        this.memoryStore = memoryStore;
        backupFolder = configuration["Memory:BackupFolder"]!;
        copiesToKeep = configuration.GetValue("Memory:BackupCopiesToKeep", 7);
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var backupPath = memoryStore.CreateBackup(backupFolder, copiesToKeep);
                logger.LogInformation("Daily memory backup written to {BackupPath}", backupPath);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Daily memory backup failed");
            }
        }
    }
}
