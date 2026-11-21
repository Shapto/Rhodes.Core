using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Settings;

/// <summary>
/// Reads and writes single values in Data/appsettings.Local.json, the settings file that wins over the one next to the core.
/// Keys use the configuration form ("LmStudio:Model"); every other key in the file, secrets included, is kept as it was.
/// </summary>
public sealed class LocalSettingsFile
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly object fileLock = new();

    public LocalSettingsFile(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// Returns the value saved in the file as text, or null when the file doesn't set it.
    /// </summary>
    public string? Read(string settingKey)
    {
        lock (fileLock)
        {
            JsonNode? currentNode = LoadRoot();
            foreach (var keyPart in settingKey.Split(':'))
            {
                currentNode = currentNode is JsonObject currentObject ? FindProperty(currentObject, keyPart) : null;
            }

            return currentNode switch
            {
                null => null,
                JsonValue jsonValue when jsonValue.TryGetValue(out string? textValue) => textValue,
                _ => currentNode.ToJsonString(),
            };
        }
    }

    /// <summary>
    /// Saves one value, creating the file and any missing sections. The file is written to a temporary file first
    /// and then moved over the old one, so a crash mid-write can't leave half a settings file.
    /// </summary>
    public void Write(string settingKey, JsonNode? settingValue)
    {
        lock (fileLock)
        {
            var root = LoadRoot();
            var keyParts = settingKey.Split(':');
            var parentObject = root;
            foreach (var keyPart in keyParts[..^1])
            {
                if (FindProperty(parentObject, keyPart) is not JsonObject childObject)
                {
                    childObject = new JsonObject();
                    parentObject[FindPropertyName(parentObject, keyPart) ?? keyPart] = childObject;
                }

                parentObject = childObject;
            }

            parentObject[FindPropertyName(parentObject, keyParts[^1]) ?? keyParts[^1]] = settingValue;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, root.ToJsonString(WriteOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
    }

    private JsonObject LoadRoot()
    {
        if (!File.Exists(FilePath))
        {
            return new JsonObject();
        }

        var fileText = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(fileText))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(fileText, documentOptions: ReadOptions) as JsonObject ?? throw new InvalidOperationException($"{FilePath} doesn't hold a JSON object.");
    }

    // Configuration keys ignore case, so "lmstudio" in the file is the same section as "LmStudio".
    private static JsonNode? FindProperty(JsonObject parentObject, string propertyName)
    {
        var existingName = FindPropertyName(parentObject, propertyName);
        return existingName is null ? null : parentObject[existingName];
    }

    private static string? FindPropertyName(JsonObject parentObject, string propertyName)
    {
        return parentObject.Select(property => property.Key).FirstOrDefault(existingName => string.Equals(existingName, propertyName, StringComparison.OrdinalIgnoreCase));
    }
}
