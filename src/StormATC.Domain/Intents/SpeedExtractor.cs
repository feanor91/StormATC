using System.Globalization;
using System.Text.RegularExpressions;

namespace StormATC.Domain.Intents;

/// <summary>Finds the speed in knots a pilot or a controller names: "speed 250", "maintain 220 knots".</summary>
public static partial class SpeedExtractor
{
    /// <summary>The speed in knots named in a transmission, or null.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    public static int? Find(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        Match match = BeforeKnots().Match(transcript);
        if (!match.Success)
        {
            match = AfterSpeed().Match(transcript);
        }

        // Digits said one by one come out spaced: "2 2 0 knots".
        return match.Success
            && int.TryParse(match.Groups["knots"].Value.Replace(" ", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out int knots)
            && knots is >= 80 and <= 400
                ? knots
                : null;
    }

    [GeneratedRegex(@"\b(?<knots>\d(?: ?\d){1,2}) ?(knots|kts|kt)\b")]
    private static partial Regex BeforeKnots();

    [GeneratedRegex(@"\bspeed (?<knots>\d(?: ?\d){1,2})\b")]
    private static partial Regex AfterSpeed();
}
