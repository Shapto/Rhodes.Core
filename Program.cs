using Rhodes.Core.Memory;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rhodes.Core.Tools;
using Rhodes.Core.Chat;

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

var personaPath = builder.Configuration["Persona:Path"] ?? Path.Combine(Path.GetDirectoryName(databasePath)!, "persona.md");
var contextTokens = builder.Configuration.GetValue("Chat:ContextTokens", 64000);
builder.Services.AddSingleton(new UploadStore(Path.Combine(Path.GetDirectoryName(databasePath)!, "uploads")));

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
    serviceProvider.GetServices<ICoreTool>(),
    personaPath,
    builder.Configuration.GetValue("Chat:MaximumThinkingCharacters", 24000),
    contextTokens,
    serviceProvider.GetRequiredService<ILogger<ChatLoop>>()));

builder.Services.AddSingleton<ChatTitler>();

builder.Services.AddSingleton(serviceProvider => new ConversationCompactor(
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<ChatLoop>(),
    serviceProvider.GetRequiredService<ChatTitler>(),
    conversationStore,
    personaPath,
    contextTokens,
    serviceProvider.GetRequiredService<ILogger<ConversationCompactor>>()));



var app = builder.Build();

app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/home.html"));
app.MapGet("/status", (ModelStatusMonitor modelStatusMonitor) => modelStatusMonitor.Current);

app.MapGet("/memory/inject-preview", (string message, ChatLoop chatLoop) => chatLoop.FindMemoriesToInject(message));

app.MapGet("/memory/find", (string text, MemoryStore store) => store.FindContaining(text, 200)
    .Select(memory => new { memory.Id, memory.Type, memory.Content }));

var webJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
app.MapPost("/memory/{memoryId}/delete", (string memoryId, MemoryStore store) => store.Delete(memoryId) ? Results.Ok() : Results.NotFound());

app.MapPost("/chat", async (ChatRequest chatRequest, ChatLoop chatLoop, ChatTitler chatTitler, ConversationManager conversationManager, ConversationCompactor conversationCompactor, ModelStatusMonitor modelStatusMonitor, CancellationToken cancellationToken) =>
{
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

app.MapPost("/chat/stream", async (ChatRequest chatRequest, ChatLoop chatLoop, ChatTitler chatTitler, ConversationManager conversationManager, ConversationCompactor conversationCompactor, ModelStatusMonitor modelStatusMonitor, HttpContext httpContext, CancellationToken cancellationToken) =>
{
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

app.Run();