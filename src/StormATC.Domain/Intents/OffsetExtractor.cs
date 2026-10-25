using System.Globalization;
using System.Text.RegularExpressions;

namespace StormATC.Domain.Intents;

/// <summary>Finds the distance and the side of a parallel offset a pilot asks for: "offset 2 miles right".</summary>
public static partial class OffsetExtractor
{
    /// <summary>The distance used when the pilot names none, in nautical miles.</summary>
    public const int DefaultMiles = 2;

    /// <summary>The distance in nautical miles named in a transmission, or null.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    public static int? FindMiles(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        Match match = BeforeMiles().Match(transcript);
        return match.Success
            && int.TryParse(match.Groups["miles"].Value.Replace(" ", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out int miles)
                ? miles
                : null;
    }

    /// <summary>True for the right of the route (the default), false when the pilot says left.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    public static bool IsRight(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return !LeftSide().IsMatch(transcript) || RightSide().IsMatch(transcript);
    }

    [GeneratedRegex(@"\b(?<miles>\d(?: ?\d)?) ?(?:miles?|nm|nautical)\b")]
    private static partial Regex BeforeMiles();

    [GeneratedRegex(@"\bleft\b")]
    private static partial Regex LeftSide();

    [GeneratedRegex(@"\bright\b")]
    private static partial Regex RightSide();
}
