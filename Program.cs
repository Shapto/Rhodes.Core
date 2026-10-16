using Rhodes.Core.Memory;

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

var app = builder.Build();
app.MapGet("/memory/search", (string query, MemoryStore store) => store.Search(query, limit: 5, includeArchive: false));
app.MapGet("/memory/recent", (MemoryStore store) => store.GetRecent(10));
app.MapGet("/memory/breakdown", (MemoryStore store) => store.GetBreakdown());
app.MapGet("/memory/review", (MemoryStore store) => store.GetNeedingReview()
    .Select(memory => new { memory.Id, memory.LegacyCategory, memory.Content }));

app.MapGet("/memory/category/{category}", (string category, MemoryStore store) => store.GetByLegacyCategory(category, 100)
    .Select(memory => new { memory.Id, memory.Content }));

app.MapPost("/memory/{memoryId}/type", (string memoryId, string type, MemoryStore store) =>
{
    if (!MemoryTypes.All.Contains(type))
    {
        return Results.BadRequest($"Type must be one of: {string.Join(", ", MemoryTypes.All)}");
    }

    return store.SetType(memoryId, type) ? Results.Ok() : Results.NotFound();
});
app.Run();