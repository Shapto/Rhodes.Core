using Microsoft.AspNetCore.Mvc;
using Rhodes.Core.Chat;
using Rhodes.Core.Documents;
using Rhodes.Core.Memory;
using Rhodes.Core.Tools;
using Rhodes.Core.Web;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Rhodes.Core.Heartbeat;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

var databasePath = builder.Configuration["Memory:DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    throw new InvalidOperationException("Set Memory:DatabasePath in appsettings.Local.json.");
}

var memoryStore = new MemoryStore(databasePath);
memoryStore.CreateBackup(builder.Configuration["Memory:BackupFolder"]!, builder.Configuration.GetValue("Memory:BackupCopiesToKeep", 7));
builder.Services.AddSingleton(memoryStore);
var decayHalfLifeDays = builder.Configuration.GetValue("Memory:DecayHalfLifeDays", 30.0);
builder.Services.AddSingleton(new MemoryRanker(memoryStore, decayHalfLifeDays));
builder.Services.AddSingleton<ICoreTool, SearchMemoryTool>();
builder.Services.AddSingleton<ICoreTool, SaveMemoryTool>();
builder.Services.AddSingleton<ICoreTool, WebSearchTool>();
builder.Services.AddSingleton<ICoreTool, WebFetchTool>();
builder.Services.AddSingleton<ICoreTool, SearchDocumentTool>();
builder.Services.AddSingleton<ICoreTool, ReadDocumentTool>();
builder.Services.AddSingleton<ICoreTool, SearchChatsTool>();
builder.Services.AddSingleton<ICoreTool, ReadChatTool>();
builder.Services.AddSingleton<ICoreTool, AskShaptoTool>();
builder.Services.AddSingleton(serviceProvider => new WebReader(builder.Configuration["GitHub:Token"], serviceProvider.GetRequiredService<ILogger<WebReader>>()));

var personaPath = builder.Configuration["Persona:Path"] ?? Path.Combine(Path.GetDirectoryName(databasePath)!, "persona.md");
var contextTokens = builder.Configuration.GetValue("Chat:ContextTokens", 64000);
builder.Services.AddSingleton(new UploadStore(Path.Combine(Path.GetDirectoryName(databasePath)!, "uploads")));
builder.Services.AddSingleton(new DocumentStore(Path.Combine(Path.GetDirectoryName(databasePath)!, "rhodes.db"), Path.Combine(Path.GetDirectoryName(databasePath)!, "documents")));

builder.Services.AddSingleton(serviceProvider => new LmStudioClient(
    builder.Configuration["LmStudio:BaseUrl"] ?? "http://localhost:1234/v1",
    builder.Configuration["LmStudio:Model"] ?? "",
    builder.Configuration["LmStudio:ApiKey"],
    serviceProvider.GetRequiredService<ILogger<LmStudioClient>>()));

builder.Services.AddSingleton(serviceProvider => new ModelStatusMonitor(
    builder.Configuration["LmStudio:BaseUrl"] ?? "http://localhost:1234/v1",
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<ILogger<ModelStatusMonitor>>()));
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ModelStatusMonitor>());

var conversationStore = new ConversationStore(Path.Combine(Path.GetDirectoryName(databasePath)!, "rhodes.db"));
builder.Services.AddSingleton(conversationStore);
builder.Services.AddSingleton<ConversationManager>();
builder.Services.AddHostedService<MemoryBackupService>();

builder.Services.AddSingleton(serviceProvider => new ChatLoop(
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<MemoryRanker>(),
    conversationStore,
    serviceProvider.GetRequiredService<UploadStore>(),
    serviceProvider.GetRequiredService<DocumentStore>(),
    serviceProvider.GetRequiredService<HeartbeatStore>(),
    serviceProvider.GetServices<ICoreTool>(),
    personaPath,
    builder.Configuration.GetValue("Chat:MaximumThinkingCharacters", 24000),
    contextTokens,
    serviceProvider.GetRequiredService<ILogger<ChatLoop>>()));

builder.Services.AddSingleton<ChatTitler>();

// The heartbeat: Closure wakes on her own when Shapto has been away a while (see HeartbeatService for the rules).
builder.Services.AddSingleton<ActivityTracker>();
builder.Services.AddSingleton(new HeartbeatStore(Path.Combine(Path.GetDirectoryName(databasePath)!, "rhodes.db")));
builder.Services.AddSingleton(serviceProvider => new HeartbeatRunner(
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<HeartbeatStore>(),
    conversationStore,
    serviceProvider.GetRequiredService<ConversationManager>(),
    serviceProvider.GetRequiredService<ChatLoop>(),
    serviceProvider.GetServices<ICoreTool>(),
    personaPath,
    builder.Configuration.GetValue("Heartbeat:AllowWeb", false),
    serviceProvider.GetRequiredService<ILogger<HeartbeatRunner>>()));
builder.Services.AddSingleton<HeartbeatService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<HeartbeatService>());

builder.Services.AddSingleton(serviceProvider => new ConversationCompactor(
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<ChatLoop>(),
    serviceProvider.GetRequiredService<ChatTitler>(),
    conversationStore,
    personaPath,
    contextTokens,
    serviceProvider.GetRequiredService<ILogger<ConversationCompactor>>()));



var app = builder.Build();

// "no-cache" makes the browser check with the core before reusing a page file, so edits show up on the first reload.
// Unchanged files still come back as a cheap "not modified", so nothing gets slower on the LAN.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = staticFileContext => staticFileContext.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapGet("/", () => Results.Redirect("/home.html"));
app.MapGet("/status", (ModelStatusMonitor modelStatusMonitor) => modelStatusMonitor.Current);

// Profile pictures live in Data\pictures
var picturesFolder = Path.Combine(Path.GetDirectoryName(databasePath)!, "pictures");
var pictureTypes = new (string Extension, string ContentType)[] { (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".webp", "image/webp"), (".gif", "image/gif") };
app.MapGet("/pictures/{personName}", (string personName) =>
{
    if (personName is not ("closure" or "shapto"))
    {
        return Results.NotFound();
    }

    var picture = pictureTypes
        .Select(pictureType => (Path: Path.Combine(picturesFolder, personName + pictureType.Extension), pictureType.ContentType))
        .FirstOrDefault(candidate => File.Exists(candidate.Path));
    return picture.Path is null ? Results.NotFound() : Results.File(picture.Path, picture.ContentType);
});

app.MapGet("/memory/inject-preview", (string message, ChatLoop chatLoop) => chatLoop.FindMemoriesToInject(message));

app.MapGet("/memory/find", (string text, MemoryStore store) => store.FindContaining(text, 200)
    .Select(memory => new { memory.Id, memory.Type, memory.Content }));

var webJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
app.MapPost("/memory/{memoryId}/delete", (string memoryId, MemoryStore store) => store.Delete(memoryId) ? Results.Ok() : Results.NotFound());

app.MapPost("/chat", async (ChatRequest chatRequest, ChatLoop chatLoop, ChatTitler chatTitler, ConversationManager conversationManager, ConversationCompactor conversationCompactor, ModelStatusMonitor modelStatusMonitor, ActivityTracker activityTracker, CancellationToken cancellationToken) =>
{
    using var chatActivity = activityTracker.BeginChat();
    var modelStatus = await modelStatusMonitor.GetFreshStatusAsync(cancellationToken);
    if (modelStatus.State != ModelStates.Online)
    {
        return Results.Problem(modelStatus.DescribeProblem(), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var conversation = string.IsNullOrEmpty(chatRequest.ConversationId) ? conversationManager.Create() : conversationManager.Get(chatRequest.ConversationId);
    if (conversation is null)
    {
        return Results.NotFound("That chat doesn't exist.");
    }

    var turnResult = await chatLoop.RunTurnAsync(conversation, chatRequest.Message, chatRequest.AttachmentIds ?? [], chatRequest.Client ?? "test console", chatRequest.Thinking ?? "auto", null, cancellationToken);
    _ = Task.Run(async () =>
    {
        await chatTitler.TitleNewChatAsync(conversation, CancellationToken.None);
        await conversationCompactor.CompactIfNeededAsync(conversation, null, CancellationToken.None);
    });
    return Results.Ok(new { conversationId = conversation.Id, turnResult });
});

app.MapPost("/chat/stream", async (ChatRequest chatRequest, ChatLoop chatLoop, ChatTitler chatTitler, ConversationManager conversationManager, ConversationCompactor conversationCompactor, ModelStatusMonitor modelStatusMonitor, ActivityTracker activityTracker, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    using var chatActivity = activityTracker.BeginChat();
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";

    async Task SendAsync(object payload)
    {
        await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, webJsonOptions)}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
    var modelStatus = await modelStatusMonitor.GetFreshStatusAsync(cancellationToken);
    if (modelStatus.State != ModelStates.Online)
    {
        await SendAsync(new { type = "error", text = modelStatus.DescribeProblem() });
        return;
    }
    var conversation = string.IsNullOrEmpty(chatRequest.ConversationId) ? conversationManager.Create() : conversationManager.Get(chatRequest.ConversationId);
    if (conversation is null)
    {
        await SendAsync(new { type = "error", text = "That chat doesn't exist anymore. Start a new one." });
        return;
    }

    await SendAsync(new { type = "conversation", text = conversation.Id });
    try
    {
        var messageText = chatRequest.Message;
        var attachmentIds = chatRequest.AttachmentIds ?? [];
        if (chatRequest.Regenerate == true)
        {
            // Regenerate reruns Shapto's last message; with new text, it's an edit of that message.
            var removedTurn = await chatLoop.RemoveLastTurnAsync(conversation, cancellationToken);
            if (removedTurn is null)
            {
                await SendAsync(new { type = "error", text = "There's no reply to regenerate in this chat." });
                return;
            }

            messageText = string.IsNullOrWhiteSpace(chatRequest.Message) ? removedTurn.MessageText : chatRequest.Message;
            attachmentIds = removedTurn.AttachmentIds;
        }

        var turnResult = await chatLoop.RunTurnAsync(conversation, messageText, attachmentIds, chatRequest.Client ?? "test page", chatRequest.Thinking ?? "auto", chatEvent => SendAsync(chatEvent), cancellationToken);
        await SendAsync(new { type = "done", text = $"{turnResult.ElapsedSeconds:F1} s", contextTokens = conversation.LastContextTokens, contextLimit = contextTokens, contextSplit = ConversationManager.EstimateContextSplit(conversation), statistics = turnResult.Statistics, sentAt = DateTimeOffset.Now });

        var newTitle = await chatTitler.TitleNewChatAsync(conversation, CancellationToken.None);
        if (newTitle is not null)
        {
            await TrySendAsync(new { type = "title", text = newTitle });
        }

        var compacted = await conversationCompactor.CompactIfNeededAsync(conversation, statusText => TrySendAsync(new { type = "status", text = statusText }), CancellationToken.None);
        if (compacted)
        {
            await TrySendAsync(new { type = "compacted", text = conversation.Title, contextTokens = conversation.LastContextTokens, contextLimit = contextTokens, contextSplit = ConversationManager.EstimateContextSplit(conversation) });
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        await SendAsync(new { type = "error", text = exception.Message });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // I pressed stop or closed the page; the turn has already been rolled back.
    }

    async Task TrySendAsync(object payload)
    {
        try
        {
            await SendAsync(payload);
        }
        catch (Exception)
        {
            // The page is gone; compaction carries on regardless.
        }
    }
});

app.MapGet("/chats", (ConversationStore conversationStore) => conversationStore.List(50));

app.MapGet("/uploads/{uploadId}", (string uploadId, UploadStore uploadStore) =>
    uploadStore.Load(uploadId) is { } upload ? Results.Text(upload.Text, "text/plain; charset=utf-8") : Results.NotFound());

app.MapPost("/uploads", async (IFormFile file, UploadStore uploadStore, CancellationToken cancellationToken) =>
{
    try
    {
        await using var fileStream = file.OpenReadStream();
        return Results.Ok(await uploadStore.SaveAsync(file.FileName, fileStream, cancellationToken));
    }
    catch (UploadRejectedException exception)
    {
        return Results.BadRequest(exception.Message);
    }
}).DisableAntiforgery();

app.MapDelete("/chats/{conversationId}", (string conversationId, ConversationManager conversationManager) => conversationManager.Delete(conversationId) switch
{
    ChatDeleteOutcome.Deleted => Results.Ok(),
    ChatDeleteOutcome.Busy => Results.Conflict("Closure is still working in that chat. Stop the reply or wait, then delete it."),
    _ => Results.NotFound(),
});

app.MapPost("/chats/{conversationId}/title", (string conversationId, TitleRequest titleRequest, ConversationManager conversationManager, ConversationStore conversationStore) =>
{
    var conversation = conversationManager.Get(conversationId);
    if (conversation is null)
    {
        return Results.NotFound();
    }
    var newTitle = titleRequest.Title.Trim();
    if (newTitle.Length == 0)
    {
        return Results.BadRequest("The title can't be empty.");
    }

    conversation.Title = newTitle.Length > 60 ? newTitle[..60] : newTitle;
    conversation.TitleIsManual = true;
    conversationStore.SaveTitle(conversation);
    return Results.Ok(new { conversation.Title });
});

app.MapGet("/chats/{conversationId}/compaction", (string conversationId, ConversationManager conversationManager) =>
    conversationManager.Get(conversationId) is { } conversation
        ? Results.Ok(new { conversation.Summary, conversation.SummaryCoversThrough, conversation.ToolResultsCollapsedBefore, conversation.LastContextTokens, MessageCount = conversation.Messages.Count })
        : Results.NotFound());

app.MapGet("/chats/{conversationId}", (string conversationId, ConversationManager conversationManager) =>
    conversationManager.Get(conversationId) is { } conversation ? Results.Ok(ConversationManager.ToView(conversation, contextTokens)) : Results.NotFound());

app.MapGet("/memory/list", (string? type, string? search, string? sort, int? offset, int? limit, MemoryStore store) =>
{
    var memoryPage = store.ListMemories(type, search, sort ?? "newest", Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 30, 1, 100));
    var memories = memoryPage.Memories
        .Select(memory => new { memory.Id, memory.Content, memory.Type, memory.Tags, memory.Source, memory.Confidence, memory.CreatedAt, memory.LastAccessedAt, memory.AccessCount, memory.NeedsReview })
        .ToList();
    return Results.Ok(new { memoryPage.Total, Memories = memories });
});
app.MapGet("/memory/statistics", (MemoryStore store) => store.GetStatistics());

app.MapGet("/memory/search", (string query, MemoryStore store) => store.Search(query, limit: 5, includeArchive: false));
app.MapGet("/memory/recent", (MemoryStore store) => store.GetRecent(10));
app.MapGet("/memory/breakdown", (MemoryStore store) => store.GetBreakdown());
app.MapGet("/memory/review", (MemoryStore store) => store.GetNeedingReview()
    .Select(memory => new { memory.Id, memory.LegacyCategory, memory.Content }));

app.MapGet("/memory/category/{category}", (string category, MemoryStore store) => store.GetByLegacyCategory(category, 100)
    .Select(memory => new { memory.Id, memory.Content }));
app.MapGet("/memory/rank", (string query, MemoryRanker ranker) => ranker.Rank(query, limit: 8, includeArchive: false, countAsAccess: false)
    .Select(rankedMemory => new
    {
        rankedMemory.Memory.Type,
        rankedMemory.Memory.Content,
        Score = Math.Round(rankedMemory.CompositeScore, 3),
        Match = Math.Round(rankedMemory.Similarity, 2),
        Recency = Math.Round(rankedMemory.Recency, 2),
        Use = Math.Round(rankedMemory.Frequency, 2),
    }));

app.MapPost("/memory/{memoryId}/type", (string memoryId, string type, MemoryStore store) =>
{
    if (!MemoryTypes.All.Contains(type))
    {
        return Results.BadRequest($"Type must be one of: {string.Join(", ", MemoryTypes.All)}");
    }

    return store.SetType(memoryId, type) ? Results.Ok() : Results.NotFound();
});

// The workbench builds its tool form from these, so a new tool shows up there without touching the page.
app.MapGet("/tools", ([FromServices] IEnumerable<ICoreTool> tools) => tools
    .Select(tool => new { tool.Name, tool.Description, Parameters = tool.ParametersSchema })
    .OrderBy(toolInfo => toolInfo.Name));

app.MapGet("/documents", (DocumentStore documentStore) => documentStore.ListRecent(40)
    .Select(document => new { document.Id, document.Name, document.Source, document.LineCount, Tokens = (int)(document.Characters * DocumentStore.TokensPerCharacter), document.CreatedAt }));

// The same map Closure gets when a big file is attached, built fresh so map fixes show on old documents too.
app.MapGet("/documents/{documentId}/map", (string documentId, DocumentStore documentStore) =>
{
    var document = documentStore.Find(documentId);
    var text = document is null ? null : documentStore.ReadAllText(documentId);
    return text is null ? Results.NotFound() : Results.Text(DocumentMap.Describe(document!, DocumentStore.SplitLines(text)), "text/plain; charset=utf-8");
});

app.MapPost("/tools/{toolName}", async (string toolName, JsonElement arguments, [FromServices] IEnumerable<ICoreTool> tools, CancellationToken cancellationToken) =>
{
    var tool = tools.FirstOrDefault(candidateTool => candidateTool.Name == toolName);
    if (tool is null)
    {
        return Results.NotFound($"No tool named {toolName}.");
    }

    try
    {
        return Results.Text(await tool.ExecuteAsync(arguments, cancellationToken));
    }
    catch (ToolArgumentException exception)
    {
        return Results.Text($"Error: {exception.Message}");
    }
});

app.MapGet("/heartbeat", (HeartbeatService heartbeatService) => heartbeatService.GetStatus());
app.MapGet("/heartbeat/ticks", (int? limit, HeartbeatStore heartbeatStore) => heartbeatStore.ListTicks(Math.Clamp(limit ?? 20, 1, 100)));
app.MapPost("/heartbeat/run", async (HeartbeatService heartbeatService) =>
{
    // Not tied to the request: closing the page shouldn't cut her tick short.
    var (outcome, tick) = await heartbeatService.RunNowAsync(CancellationToken.None);
    return outcome switch
    {
        ManualTickOutcome.Ran => Results.Ok(tick),
        ManualTickOutcome.ModelUnavailable => Results.Problem("The model isn't online, so she can't wake up right now.", statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict("She's busy: a chat or another tick is running."),
    };
});

app.MapGet("/journal", (int? limit, int? offset, HeartbeatStore heartbeatStore) =>
    Results.Ok(new { Total = heartbeatStore.CountJournal(), Entries = heartbeatStore.ListJournal(Math.Clamp(limit ?? 10, 1, 50), Math.Max(0, offset ?? 0)) }));
app.MapDelete("/journal/{entryId:long}", (long entryId, HeartbeatStore heartbeatStore) => heartbeatStore.DeleteJournalEntry(entryId) ? Results.Ok() : Results.NotFound());

app.Run();