using System.Text;

namespace Rhodes.Core.Chat;

/// <summary>
/// Notices when a model's thinking goes round in a loop: the same passage written again and again word for word.
/// Works the same for every model, since it only looks at the text. Every few hundred characters it takes the newest
/// stretch and counts how often it already appeared; three times means a loop (twice can be a deliberate re-read
/// of the log line or quote she's working on).
/// </summary>
public sealed class RepetitionDetector
{
    // The stretch compared: long enough that ordinary phrases and repeated names don't match by chance.
    private const int ProbeCharacters = 200;

    // How much new text arrives between checks; checking on every token would be wasted work.
    private const int CheckEveryCharacters = 400;

    private const int LoopRepeats = 3;

    private readonly StringBuilder thinkingText = new();
    private int charactersSinceCheck;

    public bool LoopDetected { get; private set; }

    /// <summary>
    /// Adds thinking text. Returns true once the text is looping.
    /// </summary>
    public bool Feed(string thinkingPiece)
    {
        if (LoopDetected)
        {
            return true;
        }

        thinkingText.Append(thinkingPiece);
        charactersSinceCheck += thinkingPiece.Length;
        if (charactersSinceCheck < CheckEveryCharacters || thinkingText.Length < ProbeCharacters * LoopRepeats)
        {
            return false;
        }

        charactersSinceCheck = 0;
        var allText = thinkingText.ToString();
        var probe = allText[^ProbeCharacters..];
        var occurrences = 0;
        var searchFrom = 0;
        while (searchFrom <= allText.Length - ProbeCharacters)
        {
            var foundAt = allText.IndexOf(probe, searchFrom, StringComparison.Ordinal);
            if (foundAt < 0)
            {
                break;
            }

            occurrences++;
            searchFrom = foundAt + ProbeCharacters;
        }

        LoopDetected = occurrences >= LoopRepeats;
        return LoopDetected;
    }
}
