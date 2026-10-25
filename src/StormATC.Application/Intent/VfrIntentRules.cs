using System.Text.RegularExpressions;
using StormATC.Domain;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Intent;

/// <summary>
/// Recognises the VFR transmissions of a controlled aerodrome: the arrival call, reporting
/// points, the legs of the circuit, touch and go, and leaving the frequency.
/// </summary>
/// <remarks>
/// <para>
/// The deterministic parser works on canonical English keywords; French transmissions are
/// rewritten into them by <see cref="FrenchTranscriptCanonicalizer"/> first. These rules therefore
/// match English words such as "downwind" and "reporting" whatever the language spoken.
/// </para>
/// <para>
/// They apply only to a VFR flight, or to a transmission that says it is VFR. An IFR pilot who
/// says "final" is not flying a circuit, and the instrument-approach rules must keep handling it.
/// </para>
/// </remarks>
internal static partial class VfrIntentRules
{
    /// <summary>
    /// Matches a VFR transmission, or returns null so the general rules decide.
    /// </summary>
    public static (PilotIntentType Type, double Confidence)? TryMatch(string transcript, AtcSession session)
    {
        // "Request flight following" asks a flight information service to follow the flight; it is not an
        // arrival call even though it names a departure and a destination.
        if (FlightFollowing().IsMatch(transcript))
        {
            return (PilotIntentType.RequestFlightFollowing, 0.95);
        }

        bool saysVfr = VfrWord().IsMatch(transcript) || LocalFlight().IsMatch(transcript)
            || MilitaryIntentRules.RegimeIn(transcript) == FlightRules.CamV;
        if (!saysVfr && !session.FlightRules.IsVisual())
        {
            return null;
        }

        // A request for taxi, a report at the holding point, or a readback of a clearance
        // ("cleared touch and go runway 08") belongs to the ground, departure and readback rules
        // even when it mentions circuits or touch and go.
        if (GroundDepartureOrReadback().IsMatch(transcript))
        {
            return null;
        }

        // The pilot repeating a circuit instruction ("reporting downwind runway 08", "joining
        // left hand downwind") is a readback, not a report that the leg has been reached.
        if (Readback().IsMatch(transcript) && CircuitWord().IsMatch(transcript))
        {
            return (PilotIntentType.PilotAcknowledgement, 0.9);
        }

        if (LeavingFrequency().IsMatch(transcript))
        {
            return (PilotIntentType.LeaveFrequency, 0.95);
        }

        // "VFR from Albi to Blagnac for a touch and go, 1500 feet, estimate E at 05, information
        // India, request joining instructions": the first call to a tower the aircraft arrives at.
        if ((saysVfr || session.FlightRules == FlightRules.CamV)
            && !transcript.Contains("taxi", StringComparison.Ordinal) && ArrivalCall().IsMatch(transcript))
        {
            return (PilotIntentType.VfrArrivalCall, 0.93);
        }

        if (!session.FlightRules.IsVisual() && !saysVfr)
        {
            return null;
        }

        if (Downwind().IsMatch(transcript))
        {
            return (PilotIntentType.ReportDownwind, 0.93);
        }

        if (OnBase().IsMatch(transcript))
        {
            return (PilotIntentType.ReportBase, 0.93);
        }

        if (OnFinal().IsMatch(transcript))
        {
            return (PilotIntentType.ReportFinal, 0.93);
        }

        if (TouchAndGoWord().IsMatch(transcript) || ManoeuvreOf(transcript) != CircuitManoeuvre.None)
        {
            return (PilotIntentType.RequestTouchAndGo, 0.93);
        }

        if (ExtractPoint(transcript) is not null)
        {
            return (PilotIntentType.ReportAtPoint, 0.9);
        }

        return null;
    }

    /// <summary>Adds the VFR entities of a transmission to the ones already extracted.</summary>
    public static IntentEntities Enrich(IntentEntities entities, string transcript)
    {
        bool circuitContext = CircuitWord().IsMatch(transcript) || transcript.Contains("orbit", StringComparison.Ordinal);

        CircuitSide? side = null;
        if (circuitContext)
        {
            if (LeftHand().IsMatch(transcript))
            {
                side = CircuitSide.Left;
            }
            else if (RightHand().IsMatch(transcript))
            {
                side = CircuitSide.Right;
            }
        }

        return entities with
        {
            DeclaresVfr = VfrWord().IsMatch(transcript) || LocalFlight().IsMatch(transcript)
                || MilitaryIntentRules.RegimeIn(transcript) == FlightRules.CamV,
            DeclaredRegime = MilitaryIntentRules.RegimeIn(transcript),
            QfeHectopascals = MilitaryIntentRules.QfeIn(transcript),
            RequestsBreak = MilitaryIntentRules.MentionsBreak(transcript),
            GearDownAndLocked = MilitaryIntentRules.MentionsGear(transcript),
            FlightSize = MilitaryIntentRules.FlightSizeIn(transcript),
            TrailMiles = MilitaryIntentRules.TrailMilesIn(transcript),
            MarsaApproach = MilitaryIntentRules.MarsaIsApproach(transcript),
            RadarApproach = MilitaryIntentRules.RadarApproachIn(transcript),
            RunwayInSight = MilitaryIntentRules.MentionsRunwayInSight(transcript),
            TouchAndGo = TouchAndGoWord().IsMatch(transcript),
            ReadyImmediate = ProcedureIntentRules.IsReadyImmediate(transcript),
            Manoeuvre = ManoeuvreOf(transcript),
            CircuitSide = side,
            InformationLetter = ExtractInformationLetter(transcript),
            Waypoint = entities.Waypoint ?? ExtractPoint(transcript),
        };
    }

    /// <summary>
    /// The reporting point named in a transmission such as "passing echo" or "estimate e at 05",
    /// as its chart designator, or null.
    /// </summary>
    internal static string? ExtractPoint(string transcript)
    {
        foreach (Match match in PointReport().Matches(transcript))
        {
            if (LetterOf(match.Groups["point"].Value) is char letter)
            {
                return letter.ToString();
            }
        }

        return null;
    }

    private static string? ExtractInformationLetter(string transcript)
    {
        foreach (Match match in Information().Matches(transcript))
        {
            if (LetterOf(match.Groups["letter"].Value) is char letter)
            {
                return letter.ToString();
            }
        }

        return null;
    }

    private static char? LetterOf(string token)
    {
        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            return char.ToUpperInvariant(token[0]);
        }

        return PhoneticAlphabet.TryGetLetter(token, out char letter) ? char.ToUpperInvariant(letter) : null;
    }

    [GeneratedRegex(@"\b(flight following|vfr following|radar following|flight information service)\b")]
    private static partial Regex FlightFollowing();

    [GeneratedRegex(@"\bvfr\b")]
    private static partial Regex VfrWord();

    [GeneratedRegex(@"\blocal (flight|circuit)s?\b")]
    private static partial Regex LocalFlight();

    [GeneratedRegex(@"\b(taxi|holding|ready for|line up|take ?off|departure|pushback|cleared)\b")]
    private static partial Regex GroundDepartureOrReadback();

    [GeneratedRegex(@"\b(reporting|joining(?! instructions)|entering)\b")]
    private static partial Regex Readback();

    [GeneratedRegex(@"\b(downwind|base|final|crosswind|circuit|echo)\b|\breporting e\b|\bjoining e\b")]
    private static partial Regex CircuitWord();

    [GeneratedRegex(@"\bleaving (the )?(frequency|circuit)\b|\bleaving circuit and frequency\b")]
    private static partial Regex LeavingFrequency();

    [GeneratedRegex(@"\b(touch and go|full stop|option|low pass|joining instructions|request join(ing)?|inbound|estimate|estimating|for landing|to land|for (the |le |un )?break|request (the |le |un )?break)\b")]
    private static partial Regex ArrivalCall();

    [GeneratedRegex(@"\bdownwind\b")]
    private static partial Regex Downwind();

    [GeneratedRegex(@"\b(on base|base (left|right)|turning base|base leg|short base)\b")]
    private static partial Regex OnBase();

    [GeneratedRegex(@"\b(on final|final leg|short final|long final|turning final|final runway|final (left|right))\b")]
    private static partial Regex OnFinal();

    [GeneratedRegex(@"\b(touch and go|request touch)\b")]
    private static partial Regex TouchAndGoWord();

    [GeneratedRegex(@"\blow pass\b")]
    private static partial Regex LowPassWord();

    [GeneratedRegex(@"\b(?:the |for the |for )?option\b")]
    private static partial Regex OptionWord();

    /// <summary>"Low pass" or "the option": the manoeuvre asked for on the runway, if any.</summary>
    private static CircuitManoeuvre ManoeuvreOf(string transcript) =>
        LowPassWord().IsMatch(transcript) ? CircuitManoeuvre.LowPass
        : OptionWord().IsMatch(transcript) ? CircuitManoeuvre.Option
        : CircuitManoeuvre.None;

    [GeneratedRegex(@"\bleft hand\b|\bleft\b")]
    private static partial Regex LeftHand();

    [GeneratedRegex(@"\bright hand\b|\bright\b")]
    private static partial Regex RightHand();

    [GeneratedRegex(@"\b(?:passing|overhead|over|abeam|reaching|estimate|estimating|report|reporting)\s+(?:point\s+)?(?<point>[a-z]+)\b(?!\s*(?:feet|foot|thousand|hundred))")]
    private static partial Regex PointReport();

    [GeneratedRegex(@"\binformation\s+(?<letter>[a-z]+)\b")]
    private static partial Regex Information();
}
