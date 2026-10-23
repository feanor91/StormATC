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

        if (CamIndia().IsMatch(transcript))
        {
            return FlightRules.CamI;
        }

        return CamTango().IsMatch(transcript) ? FlightRules.CamT : null;
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

    /// <summary>The radar approach a pilot names: PAR (or GCA) or SRA.</summary>
    public static Domain.Approach.RadarApproachKind? RadarApproachIn(string transcript)
    {
        if (ParWord().IsMatch(transcript))
        {
            return Domain.Approach.RadarApproachKind.Par;
        }

        return SraWord().IsMatch(transcript) ? Domain.Approach.RadarApproachKind.Sra : null;
    }

    /// <summary>True when the pilot asks for something ("request PAR"): a bare mention is not a request.</summary>
    public static bool AsksForRadarApproach(string transcript) =>
        RadarApproachIn(transcript) is not null && RequestWord().IsMatch(transcript);

    /// <summary>True when the pilot says the runway or the field is in sight.</summary>
    public static bool MentionsRunwayInSight(string transcript) => InSight().IsMatch(transcript);

    [GeneratedRegex(@"\b(?:par|gca|precision radar|precision approach)\b")]
    private static partial Regex ParWord();

    [GeneratedRegex(@"\b(?:sra|surveillance radar|surveillance approach)\b")]
    private static partial Regex SraWord();

    [GeneratedRegex(@"\b(?:request|requesting|demande|demandons)\b")]
    private static partial Regex RequestWord();

    [GeneratedRegex(@"\b(?:runway|field|airfield) in sight\b")]
    private static partial Regex InSight();

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

    [GeneratedRegex(@"\bcam (?:tango|t)\b")]
    private static partial Regex CamTango();

    /// <summary>True when the pilot asks for a MARSA departure or approach.</summary>
    public static bool AsksForMarsa(string transcript) =>
        MarsaWord().IsMatch(transcript) && RequestWord().IsMatch(transcript);

    /// <summary>True for a MARSA approach (otherwise it is a departure).</summary>
    public static bool MarsaIsApproach(string transcript) => MarsaApproachWord().IsMatch(transcript);

    /// <summary>The number of aircraft the pilot gave ("flight of 2"), or null.</summary>
    public static int? FlightSizeIn(string transcript)
    {
        Match match = FlightOf().Match(transcript);
        return match.Success && int.TryParse(match.Groups[1].Value, out int size) ? size : null;
    }

    /// <summary>The trail distance the pilot gave ("trail 3 miles"), or null.</summary>
    public static int? TrailMilesIn(string transcript)
    {
        Match match = TrailMiles().Match(transcript);
        return match.Success && int.TryParse(match.Groups[1].Value, out int miles) ? miles : null;
    }

    [GeneratedRegex(@"\bmarsa\b")]
    private static partial Regex MarsaWord();

    [GeneratedRegex(@"\bmarsa (?:approach|approche)\b")]
    private static partial Regex MarsaApproachWord();

    [GeneratedRegex(@"\bflight of (\d+)\b")]
    private static partial Regex FlightOf();

    [GeneratedRegex(@"\btrail (\d+)\b")]
    private static partial Regex TrailMiles();

    [GeneratedRegex(@"\bcam (?:india|i)\b")]
    private static partial Regex CamIndia();

    [GeneratedRegex(@"\b(?:request|requesting|change|changing|switch|switching|passer|passe|passons|demande|demandons|regime)\b")]
    private static partial Regex ChangeWord();

    [GeneratedRegex(@"\b(?:taxi|holding|ready|line ?up|take ?off|cleared|landing|final|downwind|base|initial|break|departure|pushback)\b")]
    private static partial Regex MovementWord();
}
