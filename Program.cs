using Rhodes.Core.Memory;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rhodes.Core.Tools;

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

var app = builder.Build();
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