using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// The everyday procedures the French phraseology manual lists beyond the main flow: backtracking a runway, the time,
// ident, speaking slower, cancelling IFR, special VFR, a transit across an aerodrome's zone, a 360, extending the
// downwind, closing the flight plan, answering a traffic information, and the traffic information itself.
public sealed partial class StormAtcStateEngine
{
    /// <summary>The shortest time between two traffic informations.</summary>
    private static readonly TimeSpan TrafficAdvisoryInterval = TimeSpan.FromMinutes(3);

    /// <summary>The visibility under which special VFR cannot be given, in metres.</summary>
    private const int SpecialVfrMinimumVisibilityMeters = 1500;

    private static AtcDecision Acknowledge(AtcEvaluationContext context, Callsign callsign, string phrase, string fact) => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.Acknowledgement,
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["acknowledgement"] = fact },
        ProhibitedFacts = ["any clearance"],
        FallbackPhraseology = phrase,
    };

    /// <summary>"Remontée de piste": the aircraft taxis along the runway to its end before departing.</summary>
    private static AtcDecision HandleBacktrack(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject("A backtrack is asked for on the ground.", Say(context).Unable(callsign, UnableReason.Airborne));
        }

        if ((context.Session.AssignedDepartureRunway ?? context.Session.AssignedArrivalRunway) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is assigned to backtrack.",
                Say(context).Unable(callsign, UnableReason.NoDepartureRunwayAssigned));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TaxiClearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = "backtrack the runway",
                ["runway"] = runway.ToComparisonValue(),
            },
            ProhibitedFacts = ["takeoff clearance", "landing clearance"],
            FallbackPhraseology = Say(context).BacktrackInstruction(callsign, runway),
        };
    }

    private static AtcDecision HandleTimeCheck(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).TimeCheck(callsign, context.CurrentTime), "the time")
            : AtcDecision.Silent();

    private static AtcDecision HandleIdent(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(
                context,
                callsign,
                Say(context).IdentAcknowledgement(callsign, !context.AircraftState.IsOnGround),
                "ident")
            : AtcDecision.Silent();

    /// <summary>"Parlez plus lentement": the controller says the last transmission again, slowly.</summary>
    private static AtcDecision HandleSpeakSlowly(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        string? lastControllerText = null;
        IReadOnlyList<ConversationTurn> conversation = context.Session.Conversation;
        for (int i = conversation.Count - 1; i >= 0; i--)
        {
            if (conversation[i].Speaker == ConversationSpeaker.Controller)
            {
                lastControllerText = conversation[i].Text;
                break;
            }
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.SayAgain,
            FallbackPhraseology = Say(context).SpeakSlowly(callsign, lastControllerText),
            AllowLanguageModelPhraseology = false,
        };
    }

    /// <summary>"Annulons l'IFR": the flight carries on visually, without an IFR clearance.</summary>
    private static AtcDecision HandleCancelIfr(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!session.FlightRules.IsInstrument() || context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "Only an IFR flight in the air cancels its IFR flight plan.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewFlightRules = session.FlightRules.IsMilitary() ? FlightRules.CamV : FlightRules.Vfr,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["acknowledgement"] = "IFR cancelled" },
            ProhibitedFacts = ["any clearance"],
            FallbackPhraseology = Say(context).CancelIfrAcknowledgement(callsign),
        };
    }

    /// <summary>"Demande VFR spécial": cleared in the zone when the visibility allows it.</summary>
    private static AtcDecision HandleSpecialVfr(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.Weather?.VisibilityMeters is int visibility && visibility < SpecialVfrMinimumVisibilityMeters)
        {
            return AtcDecision.Reject(
                $"Special VFR needs a visibility of at least {SpecialVfrMinimumVisibilityMeters} m; it is {visibility} m.",
                Say(context).SpecialVfrRefused(callsign, visibility));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewFlightRules = VisualRules(context),
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["clearance"] = "special VFR in the zone" },
            ProhibitedFacts = ["landing clearance", "takeoff clearance"],
            FallbackPhraseology = Say(context).SpecialVfrClearance(callsign),
        };
    }

    /// <summary>"Demande transit": cleared to cross the zone over the aerodrome at a given altitude.</summary>
    private static AtcDecision HandleTransit(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject("A transit is asked for in flight.", Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        int elevation = (context.DestinationAirport ?? context.DepartureAirport)?.ElevationFeet ?? 0;
        int altitudeFeet = context.VfrProfile is { CircuitHeightFeetAmsl: int circuitHeight and > 0 }
            ? circuitHeight + 500
            : elevation + 2000;
        Altitude altitude = Altitude.FromFeet((altitudeFeet + 99) / 100 * 100);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewFlightRules = VisualRules(context),
            NewVfrTransit = true,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["clearance"] = "transit overhead the aerodrome",
                ["altitude"] = altitude.ToComparisonValue(),
            },
            ProhibitedFacts = ["landing clearance"],
            FallbackPhraseology = Say(context).TransitClearance(callsign, altitude, context.Weather?.QnhHectopascals),
        };
    }

    /// <summary>Overhead the aerodrome on a transit: nothing to join, carry on.</summary>
    private static AtcDecision TransitOverhead(AtcEvaluationContext context, Callsign callsign) => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.Acknowledgement,
        NewVfrTransit = false,
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["instruction"] = "continue, report leaving the zone" },
        ProhibitedFacts = ["landing clearance", "circuit join"],
        FallbackPhraseology = Say(context).TransitContinue(callsign),
    };

    /// <summary>"Demande un 360": approved, then report downwind again.</summary>
    private static AtcDecision HandleOrbit(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject("A 360 is flown in the air.", Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        return Acknowledge(
            context,
            callsign,
            Say(context).OrbitApproved(callsign, intent.Entities.CircuitSide),
            "360 approved");
    }

    private static AtcDecision HandleExtendDownwind(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject("The downwind is extended in the air.", Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        return Acknowledge(context, callsign, Say(context).ExtendDownwindApproved(callsign), "extend downwind");
    }

    private static AtcDecision HandleFlightPlanClosure(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).FlightPlanClosed(callsign), "flight plan closed")
            : AtcDecision.Silent();

    /// <summary>"Je surveille" / "trafic en vue": the controller takes note.</summary>
    private static AtcDecision HandleTrafficLookout(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).Acknowledgement(callsign), "traffic")
            : AtcDecision.Silent();

    /// <summary>
    /// A controller who works the flight in the air (information, departure, centre, approach) tells it about traffic
    /// close to it: the clock position, the distance and the relative altitude, at most once in three minutes.
    /// </summary>
    private static AtcDecision? EvaluateTrafficInformation(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        Aircraft.AircraftState aircraft = context.AircraftState;

        if (aircraft.IsOnGround
            || session.Callsign is not Callsign callsign
            || session.CurrentController is not (ControllerPosition.Information or ControllerPosition.Center
                or ControllerPosition.Departure or ControllerPosition.Approach)
            || (session.FlightFollowing is null && !session.FlightRules.IsInstrument())
            || (session.TrafficAdvisedAt is DateTimeOffset last && context.CurrentTime - last < TrafficAdvisoryInterval))
        {
            return null;
        }

        TrafficContact? nearest = null;
        double nearestMiles = double.MaxValue;
        foreach (TrafficContact contact in context.Traffic)
        {
            if (contact.IsOnGround || Math.Abs(contact.AltitudeFeet - aircraft.IndicatedAltitudeFeet) > 1000)
            {
                continue;
            }

            double miles = aircraft.Position.DistanceNauticalMilesTo(contact.Position);
            if (miles <= 6 && miles < nearestMiles)
            {
                nearest = contact;
                nearestMiles = miles;
            }
        }

        if (nearest is null)
        {
            return null;
        }

        double relative = (aircraft.Position.TrueBearingTo(nearest.Position).Degrees - aircraft.TrueHeadingDegrees + 360) % 360;
        int clock = (int)Math.Round(relative / 30.0) % 12;
        clock = clock == 0 ? 12 : clock;
        int relativeAltitude = (int)Math.Round(nearest.AltitudeFeet - aircraft.IndicatedAltitudeFeet);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            TrafficAdvisedAt = context.CurrentTime,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["traffic"] = $"{clock} o'clock, {Math.Max(1, (int)Math.Round(nearestMiles))} miles",
            },
            ProhibitedFacts = ["any clearance", "any altitude assignment"],
            FallbackPhraseology = Say(context).TrafficInformation(callsign, clock, Math.Max(1, (int)Math.Round(nearestMiles)), relativeAltitude),
        };
    }
}
