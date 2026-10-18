using Microsoft.AspNetCore.Mvc;
using Rhodes.Core.Approvals;
using Rhodes.Core.Chat;
using Rhodes.Core.Documents;
using Rhodes.Core.Memory;
using Rhodes.Core.Tools;
using Rhodes.Core.Web;
using Rhodes.Core.Workspace;
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

// Her workspace (a sandbox folder for code) and the approval cards that guard writes, commands and the web.
var dataFolder = Path.GetDirectoryName(databasePath)!;
builder.Services.AddSingleton(new WorkspaceFolder(builder.Configuration["Workspace:Path"] ?? Path.Combine(dataFolder, "workspace")));
builder.Services.AddSingleton(serviceProvider => new ApprovalService(Path.Combine(dataFolder, "rhodes.db"), serviceProvider.GetRequiredService<ILogger<ApprovalService>>()));
builder.Services.AddSingleton<ICoreTool, ListFilesTool>();
builder.Services.AddSingleton<ICoreTool, ReadFileTool>();
builder.Services.AddSingleton<ICoreTool, WriteFileTool>();
builder.Services.AddSingleton<ICoreTool, EditFileTool>();
builder.Services.AddSingleton<ICoreTool, DeleteFileTool>();
builder.Services.AddSingleton<ICoreTool, RunCommandTool>();
builder.Services.AddSingleton<ICoreTool, CloneRepoTool>();
builder.Services.AddSingleton<ICoreTool, LittleHandyTool>();

// Sandboxed Python for her math (see PythonSandbox for the three layers).
builder.Services.AddSingleton(new Rhodes.Core.Sandbox.PythonSandbox(
    builder.Configuration["Python:Path"] ?? Path.Combine(dataFolder, "python", "python.exe"),
    Path.Combine(dataFolder, "python-sandbox")));
builder.Services.AddSingleton<ICoreTool, RunPythonTool>();

// The behavior test set: saved prompts with expected behavior, run from the workbench.
builder.Services.AddSingleton(serviceProvider => new Rhodes.Core.Testing.BehaviorTestRunner(
    serviceProvider.GetRequiredService<ChatLoop>(),
    serviceProvider.GetRequiredService<ConversationManager>(),
    serviceProvider.GetRequiredService<ApprovalService>(),
    serviceProvider.GetRequiredService<ActivityTracker>(),
    serviceProvider.GetRequiredService<WorkspaceFolder>(),
    dataFolder,
    serviceProvider.GetRequiredService<ILogger<Rhodes.Core.Testing.BehaviorTestRunner>>()));
builder.Services.AddSingleton(serviceProvider => new WebReader(builder.Configuration["GitHub:Token"], serviceProvider.GetRequiredService<ILogger<WebReader>>()));

var personaPath = builder.Configuration["Persona:Path"] ?? Path.Combine(Path.GetDirectoryName(databasePath)!, "persona.md");
Rhodes.Core.Chat.SystemPrompt.EnsureCodeStyleFile(personaPath);
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
    serviceProvider.GetRequiredService<ApprovalService>(),
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
    serviceProvider.GetRequiredService<ApprovalService>(),
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
    serviceProvider.GetRequiredService<MemoryExtractor>(),
    conversationStore,
    personaPath,
    contextTokens,
    serviceProvider.GetRequiredService<ILogger<ConversationCompactor>>()));

// Memory upkeep: filing durable facts after a chat goes quiet (and before compaction), and Closure's cleanup proposals.
builder.Services.AddSingleton<MemoryExtractor>();
builder.Services.AddHostedService<MemoryPassService>();
builder.Services.AddSingleton(serviceProvider => new MemoryReviewer(
    Path.Combine(Path.GetDirectoryName(databasePath)!, "rhodes.db"),
    serviceProvider.GetRequiredService<MemoryStore>(),
    serviceProvider.GetRequiredService<LmStudioClient>(),
    serviceProvider.GetRequiredService<ActivityTracker>(),
    serviceProvider.GetRequiredService<ModelStatusMonitor>(),
    serviceProvider.GetRequiredService<HeartbeatRunner>(),
    serviceProvider.GetRequiredService<ILogger<MemoryReviewer>>()));



var app = builder.Build();

// Every page and API call needs the access token (see AccessGate); this runs before the pages are served.
var accessGate = new Rhodes.Core.Security.AccessGate(
    Rhodes.Core.Security.AccessGate.LoadOrCreateToken(builder.Configuration["Security:AccessToken"], Path.GetDirectoryName(databasePath)!, app.Logger),
    builder.Configuration.GetValue("Security:AllowLocalhost", true));
app.Use(accessGate.InvokeAsync);

app.MapPost("/auth/login", async (Rhodes.Core.Security.LoginRequest loginRequest, HttpContext httpContext) =>
{
    if (!accessGate.IsCorrect(loginRequest.Token?.Trim()))
    {
        // A pause after each wrong token makes guessing slow.
        await Task.Delay(1000);
        return Results.Problem("That token isn't right.", statusCode: StatusCodes.Status401Unauthorized);
    }

    httpContext.Response.Cookies.Append(Rhodes.Core.Security.AccessGate.CookieName, loginRequest.Token!.Trim(), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Expires = DateTimeOffset.Now.AddYears(1),
        Path = "/",
    });
    return Results.Ok();
});
app.MapPost("/auth/logout", (HttpContext httpContext) =>
{
    httpContext.Response.Cookies.Delete(Rhodes.Core.Security.AccessGate.CookieName);
    return Results.Ok();
});

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

        // Compaction normally runs after a reply; this catches a chat that grew past the limit in a turn that failed.
        if (await conversationCompactor.CompactIfNeededAsync(conversation, statusText => SendAsync(new { type = "status", text = statusText }), cancellationToken))
        {
            await SendAsync(new { type = "compacted", text = conversation.Title, contextTokens = conversation.LastContextTokens, contextLimit = contextTokens, contextSplit = ConversationManager.EstimateContextSplit(conversation) });
        }

        var userMessageIndex = conversation.Messages.Count;
        var turnResult = await chatLoop.RunTurnAsync(conversation, messageText, attachmentIds, chatRequest.Client ?? "test page", chatRequest.Thinking ?? "auto", chatEvent => SendAsync(chatEvent), cancellationToken);
        await SendAsync(new { type = "done", text = $"{turnResult.ElapsedSeconds:F1} s", userMessageIndex, replyMessageIndex = conversation.Messages.Count - 1, contextTokens = conversation.LastContextTokens, contextLimit = contextTokens, contextSplit = ConversationManager.EstimateContextSplit(conversation), statistics = turnResult.Statistics, sentAt = DateTimeOffset.Now });

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

app.MapPost("/chats/{conversationId}/messages/{messageIndex:int}/pin", (string conversationId, int messageIndex, PinRequest pinRequest, ConversationManager conversationManager) =>
{
    var conversation = conversationManager.Get(conversationId);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    return conversationManager.SetPinned(conversation, messageIndex, pinRequest.Pinned) ? Results.Ok() : Results.Conflict("That message can't be pinned right now (wait until her reply is finished).");
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

    // Tools that write or run commands only run from a chat, where Shapto approves each call.
    // Running them here would skip the approval card, for anyone on the network. Web tools are fine to try here.
    if (tool is IApprovalRequired approvalRequired && approvalRequired.DescribeApproval(arguments) is { Category: not ApprovalCategories.Web })
    {
        return Results.Text($"Error: {toolName} changes files or runs commands, so it only runs from a chat with Closure, where you approve it.");
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

app.MapPost("/approvals/{requestId}", (string requestId, ApprovalAnswer approvalAnswer, ApprovalService approvalService) =>
{
    var decision = approvalAnswer.Decision switch
    {
        "once" => ApprovalDecision.AllowOnce,
        "always" => ApprovalDecision.AlwaysAllow,
        _ => ApprovalDecision.Deny,
    };
    return approvalService.Resolve(requestId, decision) ? Results.Ok() : Results.NotFound("That request isn't waiting anymore (answered, timed out, or the reply was stopped).");
});
app.MapGet("/approvals/rules", (ApprovalService approvalService) => approvalService.ListRules());
app.MapDelete("/approvals/rules/{ruleId:long}", (long ruleId, ApprovalService approvalService) => approvalService.DeleteRule(ruleId) ? Results.Ok() : Results.NotFound());

app.MapGet("/brief", (HeartbeatStore heartbeatStore) => heartbeatStore.ListActiveBriefCards());
app.MapPost("/brief/{cardId:long}", (long cardId, BriefCardUpdate briefCardUpdate, HeartbeatStore heartbeatStore) =>
    !BriefCardStatuses.SetByShapto.Contains(briefCardUpdate.Status) ? Results.BadRequest("Status must be useful, not_useful, saved or dismissed.") : heartbeatStore.SetBriefCardStatus(cardId, briefCardUpdate.Status) ? Results.Ok() : Results.NotFound());

app.MapGet("/memory/proposals", (MemoryReviewer memoryReviewer) => Results.Ok(new { Status = memoryReviewer.GetStatus(), Proposals = memoryReviewer.ListOpenWithMemories() }));
app.MapPost("/memory/proposals/run", (MemoryReviewer memoryReviewer) => memoryReviewer.Start() ? Results.Ok() : Results.Conflict("A review is already running."));
app.MapPost("/memory/proposals/{proposalId:long}", (long proposalId, ProposalDecision proposalDecision, MemoryReviewer memoryReviewer) =>
    Results.Ok(new { Outcome = memoryReviewer.Decide(proposalId, proposalDecision.Decision == "accept") }));

app.MapGet("/tests", (Rhodes.Core.Testing.BehaviorTestRunner behaviorTestRunner) =>
{
    var (status, results) = behaviorTestRunner.GetResults();
    return Results.Ok(new { Status = status, Tests = behaviorTestRunner.LoadTests(), Results = results });
});
app.MapPost("/tests/run", (string? id, Rhodes.Core.Testing.BehaviorTestRunner behaviorTestRunner) =>
    behaviorTestRunner.Start(string.IsNullOrWhiteSpace(id) ? null : id) ? Results.Ok() : Results.Conflict("Tests are already running."));

app.Run();