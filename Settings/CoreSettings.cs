using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Rhodes.Core.Chat;

namespace Rhodes.Core.Settings;

public static class SettingKinds
{
    public const string Choice = "choice";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Text = "text";
}

/// <summary>
/// One setting the home page can edit. Live settings apply right away; the rest are read at startup
/// and need a core restart (the page shows RESTART PENDING until then).
/// </summary>
public sealed record SettingDefinition(string Key, string Label, string Kind, string Description, bool Live, string DefaultValue, IReadOnlyList<string>? Choices = null, double? Minimum = null);

public sealed record SettingView(string Key, string Label, string Kind, string Description, bool Live, IReadOnlyList<string>? Choices, string Value, bool RestartPending);

/// <summary>
/// The settings shown on the home page, read from and saved to Data/appsettings.Local.json.
/// Secrets (the access token, API keys) and the database path are left out on purpose: those stay hand-edited.
/// </summary>
public sealed class CoreSettings
{
    public static readonly IReadOnlyList<SettingDefinition> Definitions =
    [
        new(ModelManager.VisionSettingKey, "Vision", SettingKinds.Choice,
            "Send attached images to the model. Auto turns it on when the model's preset in models.ini loads an mmproj file.",
            Live: true, VisionModes.Auto, [VisionModes.Auto, VisionModes.On, VisionModes.Off]),
        new(ModelManager.ContextSettingKey, "Context tokens (fallback)", SettingKinds.Number,
            "Context size Closure plans with when the model's preset doesn't set ctx-size. Must match what llama-server really uses.",
            Live: true, "64000", Minimum: 4096),
        new("Chat:MaximumThinkingCharacters", "Thinking limit (characters)", SettingKinds.Number,
            "How much reasoning one step may write before it's cut off (Code thinking always gets at least 36000).",
            Live: false, "24000", Minimum: 1000),
        new("Heartbeat:AllowWeb", "Heartbeat may use the web", SettingKinds.Boolean,
            "Lets her search and fetch pages on her own while you're away.",
            Live: false, "false"),
        new("Memory:DecayHalfLifeDays", "Memory recency half-life (days)", SettingKinds.Number,
            "How fast an unused memory's recency score halves in the ranking.",
            Live: false, "30", Minimum: 1),
        new("Memory:BackupCopiesToKeep", "Memory backups to keep", SettingKinds.Number,
            "Copies of memory.db kept in Data/backups (one per start).",
            Live: false, "7", Minimum: 1),
        new("Knowledge:Path", "Knowledge folder", SettingKinds.Text,
            "Folder indexed for search_knowledge. Empty means RhodesIsland/Knowledge.",
            Live: false, ""),
        new("Workspace:Path", "Workspace folder", SettingKinds.Text,
            "Her sandbox folder for code. Empty means Data/workspace.",
            Live: false, ""),
        // The model server's address now lives per engine in Data/engines.ini (see the ENGINE panel).
        new(Rhodes.Core.Tools.HavenConnection.HostSettingKey, "haven address", SettingKinds.Text,
            "haven's host name or IP for Closure's haven tools (SSH with this computer's existing key).",
            Live: true, "haven"),
        new(Rhodes.Core.Tools.HavenConnection.UserSettingKey, "haven user", SettingKinds.Text,
            "Your account name on haven. Empty means the same name as here (or what ~/.ssh/config says).",
            Live: true, ""),
        new(Rhodes.Core.Tools.HavenConnection.PortSettingKey, "haven SSH port", SettingKinds.Text,
            "Empty means 22 (or what ~/.ssh/config says).",
            Live: true, ""),
        new(Rhodes.Core.Tools.HavenConnection.DockerCommandSettingKey, "haven docker command", SettingKinds.Text,
            "How docker runs on haven: docker when your user is in the docker group, or sudo -n docker.",
            Live: true, "docker"),
    ];

    private readonly LocalSettingsFile localSettingsFile;
    private readonly IConfiguration startupConfiguration;
    private readonly ModelManager modelManager;
    private readonly ILogger<CoreSettings> logger;

    public CoreSettings(LocalSettingsFile localSettingsFile, IConfiguration startupConfiguration, ModelManager modelManager, ILogger<CoreSettings> logger)
    {
        this.localSettingsFile = localSettingsFile;
        this.startupConfiguration = startupConfiguration;
        this.modelManager = modelManager;
        this.logger = logger;
    }

    public IReadOnlyList<SettingView> List()
    {
        return Definitions
            .Select(definition =>
            {
                // The configuration the core started with doesn't reload, so the file shows saved-but-not-yet-applied values.
                var startupValue = startupConfiguration[definition.Key] ?? definition.DefaultValue;
                var savedValue = localSettingsFile.Read(definition.Key) ?? startupValue;
                var restartPending = !definition.Live && !string.Equals(Normalize(definition, savedValue), Normalize(definition, startupValue), StringComparison.Ordinal);
                return new SettingView(definition.Key, definition.Label, definition.Kind, definition.Description, definition.Live, definition.Choices, Normalize(definition, savedValue), restartPending);
            })
            .ToList();
    }

    /// <summary>
    /// Checks and saves one setting, applying it at once when it's live. Returns an error text, or null on success.
    /// </summary>
    public string? Save(string settingKey, string? settingText)
    {
        var definition = Definitions.FirstOrDefault(candidate => candidate.Key == settingKey);
        if (definition is null)
        {
            return $"\"{settingKey}\" isn't a setting the home page can change.";
        }

        settingText = settingText?.Trim() ?? "";
        JsonNode? settingValue;
        switch (definition.Kind)
        {
            case SettingKinds.Number:
                if (!double.TryParse(settingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue) || (definition.Minimum is { } minimum && numberValue < minimum))
                {
                    return definition.Minimum is { } lowest ? $"{definition.Label} needs a number of at least {lowest}." : $"{definition.Label} needs a number.";
                }

                settingValue = numberValue == Math.Floor(numberValue) ? JsonValue.Create((long)numberValue) : JsonValue.Create(numberValue);
                break;
            case SettingKinds.Boolean:
                if (!bool.TryParse(settingText, out var booleanValue))
                {
                    return $"{definition.Label} needs true or false.";
                }

                settingValue = JsonValue.Create(booleanValue);
                break;
            case SettingKinds.Choice:
                if (definition.Choices?.Contains(settingText) != true)
                {
                    return $"{definition.Label} has to be one of: {string.Join(", ", definition.Choices ?? [])}.";
                }

                settingValue = JsonValue.Create(settingText);
                break;
            default:
                // An empty text setting is removed, so the built-in default applies again.
                settingValue = settingText.Length == 0 ? null : JsonValue.Create(settingText);
                break;
        }

        localSettingsFile.Write(definition.Key, settingValue);
        logger.LogInformation("Setting {Key} changed to {Value}", definition.Key, settingText);
        ApplyLive(definition.Key, settingText);
        return null;
    }

    /// <summary>
    /// Restarts the published core in place: a detached shell waits until this process has exited and freed the port,
    /// then starts the same executable with the same arguments. Only works for the published Linux build
    /// (not under a debugger or dotnet run). Returns an error text, or null when the restart is under way.
    /// </summary>
    public static string? StartRestart(IHostApplicationLifetime applicationLifetime)
    {
        var executablePath = Environment.ProcessPath;
        if (!OperatingSystem.IsLinux() || Debugger.IsAttached || executablePath is null || !Path.GetFileName(executablePath).StartsWith("Rhodes.Core", StringComparison.Ordinal))
        {
            return "Restarting from the page only works for the published core started by Start-Rhodes.sh. Restart it the usual way.";
        }

        var restartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        // "$0" is the executable and "$@" the original arguments, so nothing needs quoting.
        // The output stays on the inherited log file (Logs/core.log).
        restartInfo.ArgumentList.Add("-c");
        restartInfo.ArgumentList.Add($"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.5; done; exec \"$0\" \"$@\"");
        restartInfo.ArgumentList.Add(executablePath);
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            restartInfo.ArgumentList.Add(argument);
        }

        Process.Start(restartInfo);
        applicationLifetime.StopApplication();
        return null;
    }

    private void ApplyLive(string settingKey, string settingText)
    {
        switch (settingKey)
        {
            case ModelManager.VisionSettingKey:
                modelManager.VisionMode = VisionModes.Parse(settingText);
                break;
            case ModelManager.ContextSettingKey:
                modelManager.FallbackContextTokens = (int)double.Parse(settingText, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static string Normalize(SettingDefinition definition, string value)
    {
        return definition.Kind switch
        {
            SettingKinds.Boolean => bool.TryParse(value, out var booleanValue) ? (booleanValue ? "true" : "false") : value,
            SettingKinds.Choice when definition.Key == ModelManager.VisionSettingKey => VisionModes.Parse(value),
            SettingKinds.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue) ? numberValue.ToString(CultureInfo.InvariantCulture) : value,
            _ => value,
        };
    }
}

public sealed record SettingChange(string Key, string? Value);
