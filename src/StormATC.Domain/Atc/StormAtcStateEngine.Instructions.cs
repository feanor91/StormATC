using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// The en-route and ground instructions of the phraseology manual a pilot asks for: a new route, a speed, a hold, RVSM,
// SELCAL and CPDLC, a tow, a runway inspection, a position report; and the conditions a controller adds to a call: the
// aerodrome's conditions to a pilot who has no ATIS, the place in the circuit sequence, and a wake turbulence caution.
public sealed partial class StormAtcStateEngine
{
    /// <summary>How far from the aerodrome a circuit aircraft is counted, in nautical miles.</summary>
    private const double CircuitTrafficRadiusNauticalMiles = 4;

    private static readonly string[] HeavyTypeMarkers =
    [
        "A380", "A330", "A340", "A350", "B747", "B744", "B748", "747", "B777", "777", "B767", "767", "B787", "787", "MD11", "MD-11",
        "IL-96", "A400", "C-5", "C-17", "C5", "C17",
    ];

    private static AtcDecision? RequiresAirborne(AtcEvaluationContext context, Callsign callsign, string what) =>
        context.AircraftState.IsOnGround
            ? AtcDecision.Reject(what, Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight))
            : null;

    /// <summary>"Demande une nouvelle route via KILOX": approved, then report the next fix.</summary>
    private static AtcDecision HandleRouteChange(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (RequiresAirborne(context, callsign, "A route change is asked for in flight.") is { } refused)
        {
            return refused;
        }

        string? fix = FixExtractor.Find(intent.SourceTranscript);
        return Acknowledge(context, callsign, Say(context).RouteChangeApproved(callsign, fix), "new route approved");
    }

    /// <summary>"Demande vitesse 250", "demande vitesse libre": the controller gives the speed.</summary>
    private static AtcDecision HandleSpeed(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (RequiresAirborne(context, callsign, "A speed is asked for in flight.") is { } refused)
        {
            return refused;
        }

        return Acknowledge(
            context,
            callsign,
            Say(context).SpeedInstruction(callsign, intent.Entities.SpeedKnots, intent.Entities.Mach, intent.Entities.FreeSpeed),
            "speed");
    }

    /// <summary>"Demande une attente sur KILOX": the controller gives a standard right-hand hold.</summary>
    private static AtcDecision HandleHold(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (RequiresAirborne(context, callsign, "A hold is asked for in flight.") is { } refused)
        {
            return refused;
        }

        return Acknowledge(
            context,
            callsign,
            Say(context).HoldInstruction(callsign, FixExtractor.Find(intent.SourceTranscript)),
            "hold");
    }

    private static AtcDecision HandleLeaveHold(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).LeaveHoldInstruction(callsign), "leave the hold")
            : AtcDecision.Silent();

    private static AtcDecision HandleRvsm(AtcEvaluationContext context, PilotIntent intent) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(
                context,
                callsign,
                Say(context).RvsmAcknowledgement(callsign, !intent.SourceTranscript.Contains("negative", StringComparison.Ordinal)),
                "RVSM status")
            : AtcDecision.Silent();

    private static AtcDecision HandleSelcal(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).SelcalCheckAcknowledgement(callsign), "SELCAL check")
            : AtcDecision.Silent();

    private static AtcDecision HandleCpdlc(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).CpdlcLogonAcknowledgement(callsign), "CPDLC logon")
            : AtcDecision.Silent();

    private static AtcDecision HandleTow(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject("An aircraft is towed on the ground.", Say(context).Unable(callsign, UnableReason.Airborne));
        }

        return Acknowledge(context, callsign, Say(context).TowClearance(callsign), "tow");
    }

    private static AtcDecision HandleRunwayInspection(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if ((context.Session.AssignedDepartureRunway ?? context.Session.AssignedArrivalRunway) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is in use to inspect.",
                Say(context).Unable(callsign, UnableReason.NoDepartureRunwayAssigned));
        }

        return Acknowledge(context, callsign, Say(context).RunwayInspectionClearance(callsign, runway), "runway inspection");
    }

    private static AtcDecision HandlePositionReport(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).Acknowledgement(callsign), "position report")
            : AtcDecision.Silent();

    // -----------------------------------------------------------------------------------------------------------
    // What a controller adds to a call.
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An arrival call that carries no ATIS letter gets the aerodrome's conditions first (runway, wind, QNH), and one
    /// that joins behind other circuit traffic gets its number in the sequence.
    /// </summary>
    private static AtcDecision HandleVfrArrivalCall(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcDecision decision = HandleVfrArrivalCallCore(context, intent);
        if (!decision.Accepted
            || context.Session.Callsign is not Callsign callsign
            || IsMilitaryCall(context, intent)
            || ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return decision;
        }

        string prefix = string.Empty;
        if (intent.Entities.InformationLetter is null)
        {
            prefix += Say(context).ArrivalConditions(
                callsign,
                runway,
                context.Weather?.WindDirection,
                context.Weather?.WindSpeedKnots,
                context.Weather?.QnhHectopascals) + " ";
        }

        return WithSequence(context, callsign, decision with { FallbackPhraseology = prefix + decision.FallbackPhraseology });
    }

    /// <summary>Adds the place in the sequence to an instruction that joins the circuit behind other traffic.</summary>
    private static AtcDecision WithSequence(AtcEvaluationContext context, Callsign callsign, AtcDecision decision)
    {
        int ahead = CircuitTrafficCount(context);
        return ahead == 0
            ? decision
            : decision with { FallbackPhraseology = Say(context).SequenceNumber(callsign, ahead + 1) + " " + decision.FallbackPhraseology };
    }

    /// <summary>How many other aircraft fly the circuit of the aerodrome: airborne, close and low.</summary>
    private static int CircuitTrafficCount(AtcEvaluationContext context)
    {
        AirportData? airport = context.DestinationAirport ?? context.RelevantAirport ?? context.DepartureAirport;
        if (airport is null)
        {
            return 0;
        }

        int count = 0;
        foreach (TrafficContact contact in context.Traffic)
        {
            if (!contact.IsOnGround
                && contact.AltitudeFeet <= airport.ElevationFeet + 2500
                && contact.Position.IsWithin(airport.ReferencePosition, CircuitTrafficRadiusNauticalMiles))
            {
                count++;
            }
        }

        return Math.Min(count, 3);
    }

    /// <summary>
    /// A caution when a heavy aircraft has just landed or taken off close to the aerodrome: the wake it leaves is
    /// dangerous for the departure that follows.
    /// </summary>
    private static string WithWakeCaution(AtcEvaluationContext context, string phrase)
    {
        AirportData? airport = context.DepartureAirport ?? context.RelevantAirport;
        if (airport is null)
        {
            return phrase;
        }

        foreach (TrafficContact contact in context.Traffic)
        {
            if (!contact.IsOnGround
                && contact.AltitudeFeet <= airport.ElevationFeet + 1500
                && contact.Position.IsWithin(airport.ReferencePosition, 3)
                && HeavyTypeMarkers.Any(marker => contact.ModelTitle.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return phrase + " " + Say(context).WakeTurbulenceCaution(contact.ModelTitle.Length > 0 ? contact.ModelTitle : null);
            }
        }

        return phrase;
    }
}
