using System.Text.RegularExpressions;
using StormATC.Domain;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Intent;

/// <summary>
/// Recognises the everyday requests of the phraseology manual that sit outside the main flow of a flight: backtracking a
/// runway, the time, ident, speaking slower, cancelling IFR, special VFR, a transit, a 360, extending the downwind,
/// closing the flight plan and answering a traffic information.
/// </summary>
/// <remarks>
/// The rules work on the canonical English keywords: <see cref="FrenchTranscriptCanonicalizer"/> turns the French
/// phrases ("remontée de piste", "annulons l'IFR", "je surveille") into them first.
/// </remarks>
internal static partial class ProcedureIntentRules
{
    /// <summary>Matches one of these requests, or returns null so the general rules decide.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    /// <param name="session">The session.</param>
    public static (PilotIntentType Type, double Confidence)? TryMatch(string transcript, AtcSession session)
    {
        if (TimeCheck().IsMatch(transcript))
        {
            return (PilotIntentType.RequestTimeCheck, 0.93);
        }

        if (SpeakSlower().IsMatch(transcript))
        {
            return (PilotIntentType.SpeakSlowly, 0.94);
        }

        if (Ident().IsMatch(transcript))
        {
            return (PilotIntentType.ReportIdent, 0.92);
        }

        if (CancelIfr().IsMatch(transcript))
        {
            return (PilotIntentType.CancelIfr, 0.94);
        }

        if (SpecialVfr().IsMatch(transcript))
        {
            return (PilotIntentType.RequestSpecialVfr, 0.94);
        }

        if (Backtrack().IsMatch(transcript))
        {
            return (PilotIntentType.RequestBacktrack, 0.93);
        }

        if (FlightPlanClosure().IsMatch(transcript))
        {
            return (PilotIntentType.RequestFlightPlanClosure, 0.94);
        }

        if (ExtendDownwind().IsMatch(transcript))
        {
            return (PilotIntentType.RequestExtendDownwind, 0.93);
        }

        if (Orbit().IsMatch(transcript))
        {
            return (PilotIntentType.RequestOrbit, 0.92);
        }

        if (Transit().IsMatch(transcript))
        {
            return (PilotIntentType.RequestTransit, 0.92);
        }

        // "Overhead" on its own is the report of a flight that was cleared to cross the zone.
        if (session.VfrTransit && Overhead().IsMatch(transcript))
        {
            return (PilotIntentType.ReportAtPoint, 0.92);
        }

        if (TrafficLookout().IsMatch(transcript))
        {
            return (PilotIntentType.ReportTrafficLookout, 0.92);
        }

        return null;
    }

    /// <summary>True when the pilot says he is ready for an immediate departure.</summary>
    /// <param name="transcript">The canonical transcript.</param>
    public static bool IsReadyImmediate(string transcript) => ReadyImmediate().IsMatch(transcript);

    [GeneratedRegex(@"\b(request time check|time check|request the time)\b")]
    private static partial Regex TimeCheck();

    [GeneratedRegex(@"\b(speak slower|speak slowly|say again slowly|speak more slowly)\b")]
    private static partial Regex SpeakSlower();

    [GeneratedRegex(@"\bsquawk(ing)? ident\b|\bident(ing)?\b(?! ?ified)")]
    private static partial Regex Ident();

    [GeneratedRegex(@"\bcancel(ling|ing)? (the )?ifr\b")]
    private static partial Regex CancelIfr();

    [GeneratedRegex(@"\b(request )?special vfr\b")]
    private static partial Regex SpecialVfr();

    [GeneratedRegex(@"\b(request )?backtrack\b")]
    private static partial Regex Backtrack();

    [GeneratedRegex(@"\b(close|closing|closure of) (the |my |our )?flight plan\b|\bflight plan closure\b")]
    private static partial Regex FlightPlanClosure();

    [GeneratedRegex(@"\b(request )?extend(ed|ing)? (the )?downwind\b")]
    private static partial Regex ExtendDownwind();

    [GeneratedRegex(@"\brequest (an )?(orbit|360)\b")]
    private static partial Regex Orbit();

    [GeneratedRegex(@"\b(request )?transit\b")]
    private static partial Regex Transit();

    [GeneratedRegex(@"\boverhead\b")]
    private static partial Regex Overhead();

    [GeneratedRegex(@"\b(looking out|traffic in sight|negative contact|no contact|traffic not in sight)\b")]
    private static partial Regex TrafficLookout();

    [GeneratedRegex(@"\bready for departure immediate\b|\bready immediate\b|\bready for immediate (departure|takeoff)\b")]
    private static partial Regex ReadyImmediate();
}
