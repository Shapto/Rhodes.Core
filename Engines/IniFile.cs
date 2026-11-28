namespace Rhodes.Core.Engines;

/// <summary>
/// One [section] of an ini file, with its keys in the order they were written.
/// </summary>
public sealed record IniSection(string Name, IReadOnlyList<KeyValuePair<string, string>> Values)
{
    /// <summary>
    /// The last value written for settingKey (case-insensitive), or null when the section doesn't set it.
    /// </summary>
    public string? Get(string settingKey)
    {
        return Values.LastOrDefault(pair => string.Equals(pair.Key, settingKey, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

/// <summary>
/// Reads the same small ini format as llama-server's models.ini: [section] lines, "key = value" lines,
/// and whole-line comments starting with ; or #. Keys before the first section are ignored.
/// </summary>
public static class IniFile
{
    public static IReadOnlyList<IniSection> Read(string filePath)
    {
        var sections = new List<IniSection>();
        string? sectionName = null;
        var sectionValues = new List<KeyValuePair<string, string>>();

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                AddSection();
                sectionName = line[1..^1].Trim();
                sectionValues = [];
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (sectionName is null || equalsIndex <= 0)
            {
                continue;
            }

            sectionValues.Add(new KeyValuePair<string, string>(line[..equalsIndex].Trim(), Unquote(line[(equalsIndex + 1)..].Trim())));
        }

        AddSection();
        return sections;

        void AddSection()
        {
            if (!string.IsNullOrEmpty(sectionName))
            {
                sections.Add(new IniSection(sectionName, sectionValues));
            }
        }
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }
}
