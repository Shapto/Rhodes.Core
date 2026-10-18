namespace Rhodes.Core.Chat;

/// <summary>
/// Her system prompt: persona.md, plus Shapto's code style from code-style.md in the same folder.
/// Every place that sends the system prompt reads it here, so the text (and llama-server's cache) always matches.
/// </summary>
public static class SystemPrompt
{
    public const string CodeStyleFileName = "code-style.md";

    private const string DefaultCodeStyle = """
        ## Shapto's code style (for any code you write for them)
        - Full descriptive names, no abbreviations: masteryPercentage, not pct. A short local named after its own type is fine (StringBuilder builder).
        - Give classes and public methods a /// <summary> comment, always multiline: /// <summary> and /// </summary> on their own lines, the text between them.
        - Keep ternaries and similar expressions on one line. Only LINQ chains (.Where, .Select, …) are split, one call per line.
        - When you give an outline with empty method bodies, list the exact API members and types each method will need.
        """;

    /// <summary>
    /// Writes the default code-style.md next to the persona if there isn't one yet, so Shapto can edit it.
    /// </summary>
    public static void EnsureCodeStyleFile(string personaPath)
    {
        var codeStylePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(personaPath))!, CodeStyleFileName);
        if (!File.Exists(codeStylePath))
        {
            File.WriteAllText(codeStylePath, DefaultCodeStyle + "\n");
        }
    }

    public static async Task<string> LoadAsync(string personaPath, CancellationToken cancellationToken)
    {
        var persona = await File.ReadAllTextAsync(personaPath, cancellationToken);
        var codeStylePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(personaPath))!, CodeStyleFileName);
        if (!File.Exists(codeStylePath))
        {
            return persona;
        }

        var codeStyle = (await File.ReadAllTextAsync(codeStylePath, cancellationToken)).Trim();
        return codeStyle.Length == 0 ? persona : persona.TrimEnd() + "\n\n" + codeStyle;
    }
}
