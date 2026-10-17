using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Tools;

namespace Rhodes.Core.Heartbeat;

/// <summary>
/// Leaves a card in Shapto's morning brief on the dashboard. Only handed to her during a heartbeat tick, and the brief holds at most 4 cards.
/// </summary>
public sealed class LeaveCardTool : ICoreTool
{
    public const int MaximumActiveCards = 4;
    private const int MaximumTitleCharacters = 70;
    private const int MaximumBodyCharacters = 450;

    private readonly HeartbeatStore heartbeatStore;
    private readonly long tickId;

    public LeaveCardTool(HeartbeatStore heartbeatStore, long tickId)
    {
        this.heartbeatStore = heartbeatStore;
        this.tickId = tickId;
    }

    public bool CardLeft { get; private set; }

    public string Name => "leave_card";

    public string Description =>
        "Leave a card in Shapto's morning brief, which they see on the dashboard when they're back: something genuinely useful, like an answer " +
        "to a question a chat left open, or where a project stopped and the next step. A short title and about 60 words. Never filler.";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "A few words, e.g. \"Gungnir recall: the timing you asked about\"." },
            ["body"] = new JsonObject { ["type"] = "string", ["description"] = "About 60 words." },
        },
        ["required"] = new JsonArray("title", "body"),
    };

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (CardLeft)
        {
            throw new ToolArgumentException("One card per tick. End with your one-line note.");
        }

        if (heartbeatStore.ListActiveBriefCards().Count >= MaximumActiveCards)
        {
            throw new ToolArgumentException("The brief already has 4 cards. Leave it; Shapto hasn't caught up yet.");
        }

        var title = Shorten(ToolArguments.GetRequiredString(arguments, "title").Trim(), MaximumTitleCharacters);
        var body = Shorten(ToolArguments.GetRequiredString(arguments, "body").Trim(), MaximumBodyCharacters);
        heartbeatStore.AddBriefCard(tickId, title, body);
        CardLeft = true;
        return Task.FromResult("The card is in the brief.");
    }

    private static string Shorten(string text, int maximumCharacters)
    {
        return text.Length > maximumCharacters ? text[..maximumCharacters].TrimEnd() + "…" : text;
    }
}