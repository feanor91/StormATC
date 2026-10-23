using System.Text.RegularExpressions;
using StormATC.Domain;

namespace StormATC.Application.Intent;

/// <summary>
/// Recognises the French military regimes, CAM Victor (à vue) and CAM India (aux instruments), in a
/// canonical transcript.
/// </summary>
internal static partial class MilitaryIntentRules
{
    /// <summary>The regime a transmission names, or null.</summary>
    public static FlightRules? RegimeIn(string transcript)
    {
        if (CamVictor().IsMatch(transcript))
        {
            return FlightRules.CamV;
        }

        return CamIndia().IsMatch(transcript) ? FlightRules.CamI : null;
    }

    /// <summary>
    /// True when the pilot asks to change regime ("request CAM India", "demande à passer en CAM Victor")
    /// and is not making a ground, departure or circuit call that merely states its regime.
    /// </summary>
    public static bool AsksRegimeChange(string transcript) =>
        RegimeIn(transcript) is not null
        && ChangeWord().IsMatch(transcript)
        && !MovementWord().IsMatch(transcript);

    /// <summary>The QFE quoted in a transmission, in hectopascals, or null.</summary>
    public static int? QfeIn(string transcript)
    {
        Match match = QfeNumber().Match(transcript);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
    }

    /// <summary>True when the pilot asks for, or says he is flying, a break arrival.</summary>
    public static bool MentionsBreak(string transcript) => BreakWord().IsMatch(transcript);

    /// <summary>True when the pilot says the gear is down and locked.</summary>
    public static bool MentionsGear(string transcript) => GearWord().IsMatch(transcript);

    /// <summary>
    /// True for "passing initial, QFE 1005": the report at the initial point, as opposed to the first call
    /// of the arrival ("request initial for the break").
    /// </summary>
    public static bool IsInitialReport(string transcript) =>
        InitialPoint().IsMatch(transcript) && !ArrivalWord().IsMatch(transcript);

    [GeneratedRegex(@"\bqfe\s*(\d{3,4})\b")]
    private static partial Regex QfeNumber();

    [GeneratedRegex(@"\bbreak\b")]
    private static partial Regex BreakWord();

    [GeneratedRegex(@"\bgear down\b")]
    private static partial Regex GearWord();

    [GeneratedRegex(@"\binitial\b(?! climb)|\bip\b")]
    private static partial Regex InitialPoint();

    [GeneratedRegex(@"\b(?:request|requesting|inbound|estimate|estimating|for)\b")]
    private static partial Regex ArrivalWord();

    [GeneratedRegex(@"\bcam (?:victor|v)\b")]
    private static partial Regex CamVictor();

    [GeneratedRegex(@"\bcam (?:india|i)\b")]
    private static partial Regex CamIndia();

    [GeneratedRegex(@"\b(?:request|requesting|change|changing|switch|switching|passer|passe|passons|demande|demandons|regime)\b")]
    private static partial Regex ChangeWord();

    [GeneratedRegex(@"\b(?:taxi|holding|ready|line ?up|take ?off|cleared|landing|final|downwind|base|initial|break|departure|pushback)\b")]
    private static partial Regex MovementWord();
}
