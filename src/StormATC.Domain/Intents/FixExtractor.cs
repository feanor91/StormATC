using System.Text.RegularExpressions;

namespace StormATC.Domain.Intents;

/// <summary>Finds the fix (a waypoint such as KILOX) a pilot names in a request for a hold or a new route.</summary>
public static partial class FixExtractor
{
    private static readonly HashSet<string> NotAFix = new(StringComparer.Ordinal)
    {
        "the", "present", "position", "runway", "level", "speed", "hold", "over", "free", "stand", "route", "ready",
        "leave", "new", "request", "direct", "and", "for", "with", "via", "now", "please", "tower", "ground",
        "center", "approach", "departure", "information",
    };

    /// <summary>The fix named in a transmission, in capitals, or null.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    public static string? Find(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        foreach (Match match in AfterKeyword().Matches(transcript))
        {
            string candidate = match.Groups["fix"].Value;
            if (!NotAFix.Contains(candidate))
            {
                return candidate.ToUpperInvariant();
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b(?:hold(?:ing)?(?: at| over)?|over|via|at|route)\s+(?=(?<fix>[a-z]{3,5})\b)")]
    private static partial Regex AfterKeyword();
}
