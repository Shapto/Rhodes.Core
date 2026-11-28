using System.Text.RegularExpressions;

namespace Rhodes.Core.Chat;

/// <summary>
/// Whether a turn gets the extra passes after her answer: Little Handy's code review, his fact check and the answer
/// check. Each costs minutes and makes the model re-read the chat afterwards. Off never runs them, On always does
/// (the old behaviour), Auto only when the risk is real (see ReviewDecider).
/// </summary>
public static class ReviewModes
{
    public const string Off = "off";
    public const string Auto = "auto";
    public const string On = "on";

    public const string SettingKey = "Chat:ReviewMode";

    public static readonly IReadOnlyList<string> All = [Off, Auto, On];

    /// <summary>
    /// A known mode, or null for anything else (null means "use the dashboard default").
    /// </summary>
    public static string? Parse(string? modeText)
    {
        return modeText?.Trim().ToLowerInvariant() switch
        {
            Off => Off,
            Auto => Auto,
            On => On,
            _ => null,
        };
    }
}

/// <summary>
/// The dashboard's default review mode, for chats that haven't picked their own. Changes on the home page apply at once.
/// </summary>
public sealed class ReviewDefaults
{
    private string mode;

    public ReviewDefaults(string? configuredMode)
    {
        mode = ReviewModes.Parse(configuredMode) ?? ReviewModes.Auto;
    }

    public string Mode
    {
        get => mode;
        set => mode = ReviewModes.Parse(value) ?? ReviewModes.Auto;
    }
}

public sealed record ReviewDecision(bool Review, string Reason);

/// <summary>
/// Auto mode's rule: review only when something in this turn is unproven.
/// In order: a file changed with nothing run after it → review; her code ran and succeeded → skip;
/// the reply names no files or code members → skip; it names some that no tool in this turn touched or showed → review;
/// otherwise (everything she names came up in her tools) → skip.
/// </summary>
public static partial class ReviewDecider
{
    private const int MaximumNamedInReason = 3;

    private static readonly HashSet<string> FileChangeToolNames = ["write_file", "edit_file", "delete_file"];

    public static ReviewDecision Decide(string mode, IReadOnlyList<ToolStep> turnToolSteps, string replyText)
    {
        if (mode == ReviewModes.Off)
        {
            return new ReviewDecision(false, "review is off for this chat");
        }

        if (mode == ReviewModes.On)
        {
            return new ReviewDecision(true, "review is on for this chat");
        }

        var lastChangeIndex = -1;
        for (var stepIndex = 0; stepIndex < turnToolSteps.Count; stepIndex++)
        {
            var toolStep = turnToolSteps[stepIndex];
            if (FileChangeToolNames.Contains(toolStep.ToolName) && !toolStep.Result.StartsWith("Error", StringComparison.Ordinal))
            {
                lastChangeIndex = stepIndex;
            }
        }

        var stepsAfterChange = turnToolSteps.Skip(lastChangeIndex + 1).ToList();
        if (lastChangeIndex >= 0 && !stepsAfterChange.Any(toolStep => toolStep.ToolName == "run_command"))
        {
            return new ReviewDecision(true, "she changed files and ran nothing afterwards");
        }

        if (stepsAfterChange.Any(IsSuccessfulRun))
        {
            return new ReviewDecision(false, "she ran her code and it succeeded");
        }

        var namedThings = FindNamedCode(replyText);
        if (namedThings.Count == 0)
        {
            return new ReviewDecision(false, "the reply names no files or code");
        }

        // Anything she names should have come up in a tool this turn: in what she asked for or in what came back.
        var toolText = string.Join("\n", turnToolSteps.Select(toolStep => toolStep.Arguments + "\n" + toolStep.Result));
        var unseenThings = namedThings
            .Where(namedThing => !WasShown(namedThing, toolText))
            .ToList();
        if (unseenThings.Count > 0)
        {
            return new ReviewDecision(true, "the reply names code no tool showed this turn: " + string.Join(", ", unseenThings.Take(MaximumNamedInReason)));
        }

        return new ReviewDecision(false, "everything the reply names came up in her tools");
    }

    /// <summary>
    /// Whether a tool showed this file or member. A Type.Member counts when both names appear (source code has them
    /// apart: "class ChatLoop" … "RunTurnAsync("); a file name has to appear as it is.
    /// </summary>
    private static bool WasShown(string namedThing, string toolText)
    {
        if (toolText.Contains(namedThing, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dotIndex = namedThing.IndexOf('.');
        var isMemberName = dotIndex > 0 && MemberNamePattern().Match(namedThing).Value == namedThing && !FileNamePattern().IsMatch(namedThing);
        return isMemberName && toolText.Contains(namedThing[..dotIndex], StringComparison.Ordinal) && toolText.Contains(namedThing[(dotIndex + 1)..], StringComparison.Ordinal);
    }

    /// <summary>
    /// A run_command that finished with exit code 0 (its result starts "Exit code 0 after …").
    /// </summary>
    private static bool IsSuccessfulRun(ToolStep toolStep)
    {
        return toolStep.ToolName == "run_command" && toolStep.Result.StartsWith("Exit code 0 ", StringComparison.Ordinal);
    }

    /// <summary>
    /// The files and code members a reply talks about: file names with a code or config extension
    /// (ChatLoop.cs, src/main.rs) and Type.Member names in backticks (`ModelManager.Select`). A bare word in backticks
    /// (`StringBuilder`) doesn't count: general advice names those without claiming anything about Shapto's code.
    /// Code blocks are skipped, since code she suggests isn't a claim about what exists.
    /// </summary>
    public static IReadOnlyList<string> FindNamedCode(string replyText)
    {
        var proseText = CodeBlockPattern().Replace(replyText, " ");
        var fileNames = FileNamePattern().Matches(proseText)
            .Select(match => Path.GetFileName(match.Value.Replace('\\', '/')));
        var memberNames = InlineCodePattern().Matches(proseText)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(inlineText => MemberNamePattern().IsMatch(inlineText))
            .Select(inlineText => MemberNamePattern().Match(inlineText).Value);
        return fileNames
            .Concat(memberNames)
            .Where(namedThing => namedThing.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeBlockPattern();

    [GeneratedRegex(@"[\w./\\-]*\w\.(cs|csproj|sln|xaml|xml|json|py|js|ts|html|css|rs|toml|java|lua|sh|ps1|ini|yaml|yml|cfg|md|sql)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"`([^`\n]{1,80})`")]
    private static partial Regex InlineCodePattern();

    // Type.Member or Type.Member(…): an uppercase name, a dot, a name.
    [GeneratedRegex(@"\b[A-Z]\w*\.[A-Za-z_]\w*")]
    private static partial Regex MemberNamePattern();
}
