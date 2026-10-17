using System.Text.Json;
using System.Text.Json.Nodes;
using Rhodes.Core.Tools;

namespace Rhodes.Core.Heartbeat;

/// <summary>
/// Her journal pen. Only handed to her during a heartbeat tick (never in chats), and one entry per tick.
/// </summary>
public sealed class WriteJournalTool : ICoreTool
{
    private const int MinimumEntryCharacters = 20;
    private const int MaximumEntryCharacters = 1200;

    private readonly HeartbeatStore heartbeatStore;
    private readonly long tickId;

    public WriteJournalTool(HeartbeatStore heartbeatStore, long tickId)
    {
        this.heartbeatStore = heartbeatStore;
        this.tickId = tickId;
    }

    public bool EntryWritten { get; private set; }

    public string Name => "write_journal";

    public string Description =>
        "Write one short entry in your journal: something you noticed or thought about, as a note to yourself (not a to-do list or questions for Shapto). " +
        "Shapto can read it on the dashboard. A few sentences, in your own voice. Not for facts about Shapto; those belong in memory.";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["entry"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The entry, at most about 150 words.",
            },
        },
        ["required"] = new JsonArray("entry"),
    };

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (EntryWritten)
        {
            throw new ToolArgumentException("You already wrote this tick's journal entry. End with your one-line note instead.");
        }

        var entryText = ToolArguments.GetRequiredString(arguments, "entry").Trim();
        if (entryText.Length < MinimumEntryCharacters)
        {
            throw new ToolArgumentException("That entry is too short to be worth keeping. Write a few real sentences, or skip the journal this time.");
        }

        if (entryText.Length > MaximumEntryCharacters)
        {
            entryText = entryText[..MaximumEntryCharacters].TrimEnd() + "…";
        }

        heartbeatStore.AddJournalEntry(tickId, entryText);
        EntryWritten = true;
        return Task.FromResult("Saved in your journal.");
    }
}