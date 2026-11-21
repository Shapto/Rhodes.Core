using System.Diagnostics;
using System.Text;
using Rhodes.Core.Documents;

namespace Rhodes.Core.Knowledge;

/// <summary>
/// Keeps knowledge.db in step with the Knowledge folder: indexes new and changed files, forgets deleted ones.
/// Every text file is indexed whatever its extension (code, configs, docs, logs, .docx, .html); binary files are skipped.
/// A file counts as changed when its last-write time or size differs from what was indexed.
/// The first subfolder of a file's path is its collection (Knowledge/RimWorld/… is in "RimWorld"); files at the top are in "General".
/// </summary>
public sealed class KnowledgeIndexer
{
    public const long MaximumFileBytes = 4 * 1024 * 1024;
    public const long MaximumArchiveBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    private readonly KnowledgeDatabase knowledgeDatabase;
    private readonly ILogger<KnowledgeIndexer> logger;

    public KnowledgeIndexer(KnowledgeDatabase knowledgeDatabase, ILogger<KnowledgeIndexer> logger)
    {
        this.knowledgeDatabase = knowledgeDatabase;
        this.logger = logger;
    }

    /// <summary>
    /// One full pass over the Knowledge folder. Safe to run again at any time; unchanged files are skipped.
    /// With logWhenUnchanged false (the periodic rescans) it only logs when something was added, changed or removed.
    /// </summary>
    public void IndexAll(CancellationToken cancellationToken, bool logWhenUnchanged = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var knowledgeFolder = knowledgeDatabase.KnowledgeFolder;
        Directory.CreateDirectory(knowledgeFolder);

        var indexedStamps = knowledgeDatabase.GetIndexedFileStamps();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var indexedCount = 0;
        var skippedCount = 0;
        var notTextCount = 0;
        var failedCount = 0;

        foreach (var fullPath in EnumerateKnowledgeFiles(knowledgeFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(knowledgeFolder, fullPath).Replace('\\', '/');
            seenPaths.Add(relativePath);

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > (TextExtractor.IsArchive(fullPath) ? MaximumArchiveBytes : MaximumFileBytes))
            {
                skippedCount++;
                continue;
            }

            var lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
            if (indexedStamps.TryGetValue(relativePath, out var stamp) && stamp.LastWriteTicks == lastWriteTicks && stamp.SizeBytes == fileInfo.Length)
            {
                continue;
            }

            try
            {
                // A file that turns out not to be text is still recorded (with no chunks), so it isn't opened again every start.
                var text = TextExtractor.ReadText(fullPath);
                var fileChunks = text is null ? [] : KnowledgeChunker.Split(fullPath, text);
                knowledgeDatabase.ReplaceFileChunks(relativePath, GetCollection(relativePath), lastWriteTicks, fileInfo.Length, fileChunks);
                if (text is null)
                {
                    notTextCount++;
                }
                else
                {
                    indexedCount++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                failedCount++;
                logger.LogWarning("Knowledge: couldn't index {Path}: {Message}", relativePath, exception.Message);
            }
        }

        var removedPaths = indexedStamps.Keys
            .Where(indexedPath => !seenPaths.Contains(indexedPath))
            .ToList();
        foreach (var removedPath in removedPaths)
        {
            knowledgeDatabase.RemoveFile(removedPath);
        }

        if (!logWhenUnchanged && indexedCount + notTextCount + failedCount + removedPaths.Count == 0)
        {
            return;
        }

        var (fileCount, chunkCount) = knowledgeDatabase.CountIndexed();
        logger.LogInformation(
            "Knowledge: {Indexed} files indexed, {Removed} removed, {Skipped} too big, {NotText} not text, {Failed} failed in {Seconds:0.0} s; library holds {Files} files in {Chunks} chunks ({Folder})",
            indexedCount, removedPaths.Count, skippedCount, notTextCount, failedCount, stopwatch.Elapsed.TotalSeconds, fileCount, chunkCount, knowledgeFolder);
    }

    /// <summary>
    /// The first folder under Knowledge, or "General" for files right in it.
    /// </summary>
    public static string GetCollection(string relativePath)
    {
        var slashIndex = relativePath.IndexOf('/');
        return slashIndex > 0 ? relativePath[..slashIndex] : "General";
    }

    /// <summary>
    /// Every file that could be text (known binary types are left out), walking folders by hand so bin, obj, .git and the like are never entered.
    /// </summary>
    private static IEnumerable<string> EnumerateKnowledgeFiles(string rootFolder)
    {
        var foldersToVisit = new Stack<string>();
        foldersToVisit.Push(rootFolder);
        while (foldersToVisit.Count > 0)
        {
            var folder = foldersToVisit.Pop();
            IEnumerable<string> subfolders;
            IEnumerable<string> files;
            try
            {
                subfolders = Directory.EnumerateDirectories(folder).ToList();
                files = Directory.EnumerateFiles(folder).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subfolder in subfolders)
            {
                if (!SkippedFolderNames.Contains(Path.GetFileName(subfolder)))
                {
                    foldersToVisit.Push(subfolder);
                }
            }

            foreach (var file in files)
            {
                if (!TextExtractor.IsKnownBinary(file))
                {
                    yield return file;
                }
            }
        }
    }
}

/// <summary>
/// Indexes the Knowledge folder in the background: once at startup (so a big first index never holds up the core starting),
/// then again every minute and whenever something asks for it (Fill-Knowledge.sh does, through POST /knowledge/reindex).
/// A rescan only reads files that are new or changed, so files dropped into Knowledge are searchable within a minute, no restart needed.
/// </summary>
public sealed class KnowledgeIndexService : BackgroundService
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(1);

    private readonly KnowledgeIndexer knowledgeIndexer;
    private readonly ILogger<KnowledgeIndexService> logger;
    private readonly SemaphoreSlim reindexSignal = new(0, 1);

    public KnowledgeIndexService(KnowledgeIndexer knowledgeIndexer, ILogger<KnowledgeIndexService> logger)
    {
        this.knowledgeIndexer = knowledgeIndexer;
        this.logger = logger;
    }

    /// <summary>
    /// Starts a rescan now instead of at the next minute. Several requests while one is waiting count as one.
    /// </summary>
    public void RequestReindex()
    {
        try
        {
            reindexSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A rescan is already requested.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var firstPass = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var logThisPass = firstPass;
            try
            {
                await Task.Run(() => knowledgeIndexer.IndexAll(stoppingToken, logThisPass), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // The core is shutting down; the next start carries on where this pass stopped.
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge: indexing failed");
            }

            firstPass = false;
            try
            {
                await reindexSignal.WaitAsync(RescanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
