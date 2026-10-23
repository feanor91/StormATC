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

    [GeneratedRegex(@"\bcam (?:victor|v)\b")]
    private static partial Regex CamVictor();

    [GeneratedRegex(@"\bcam (?:india|i)\b")]
    private static partial Regex CamIndia();

    [GeneratedRegex(@"\b(?:request|requesting|change|changing|switch|switching|passer|passe|passons|demande|demandons|regime)\b")]
    private static partial Regex ChangeWord();

    [GeneratedRegex(@"\b(?:taxi|holding|ready|line ?up|take ?off|cleared|landing|final|downwind|base|initial|break|departure|pushback)\b")]
    private static partial Regex MovementWord();
}
