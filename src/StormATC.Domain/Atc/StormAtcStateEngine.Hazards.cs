using StormATC.Domain.Airports;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// What keeps a flight from going where it asks, or turns it away by the controller's own decision: a saturated arrival
// (flow control), an active military area, bad weather. Plus the parallel offset and the CPDLC failure of the phraseology
// manual, and the radar sequence of the arrivals.
public sealed partial class StormAtcStateEngine
{
    /// <summary>The distance kept around a hazard, in nautical miles.</summary>
    private const double HazardMarginMiles = 3;

    /// <summary>How far ahead of the aircraft a hazard is looked for, in nautical miles.</summary>
    private const double HazardLookaheadMiles = 30;

    /// <summary>The largest parallel offset a controller approves, in nautical miles.</summary>
    private const int MaximumOffsetMiles = 20;

    /// <summary>How far from the destination arriving aircraft are counted in the radar sequence, in nautical miles.</summary>
    private const double SequenceRadiusNauticalMiles = 25;

    /// <summary>True when the destination cannot take more arrivals: a hold is ordered or the circuit is full.</summary>
    private static bool ArrivalSaturated(AtcEvaluationContext context) =>
        context.Session.Hold is not null || CircuitTrafficCount(context) >= 3;

    /// <summary>A refusal when an instrument flight asks for a shorter route and the arrival is saturated, or null.</summary>
    private static AtcDecision? RefuseForFlowControl(AtcEvaluationContext context, Callsign callsign) =>
        context.Session.FlightRules.IsInstrument() && ArrivalSaturated(context)
            ? AtcDecision.Reject("The arrival is saturated: flow control.", Say(context).FlowControlRefusal(callsign))
            : null;

    /// <summary>A refusal when the path to a fix crosses an active hazard at the level of the flight, or null.</summary>
    private static AtcDecision? RefuseForHazard(AtcEvaluationContext context, Callsign callsign, string? fix)
    {
        if (fix is null || FixPosition(context.Session, fix) is not GeoPosition target)
        {
            return null;
        }

        foreach (HazardArea hazard in context.Hazards)
        {
            if (hazard.IsActive
                && hazard.CoversAltitude(context.AircraftState.IndicatedAltitudeFeet)
                && hazard.CrossesPath(context.AircraftState.Position, target, HazardMarginMiles))
            {
                return AtcDecision.Reject(
                    $"The path to {fix} crosses {hazard.Name}.",
                    Say(context).RouteRefusedHazard(callsign, hazard));
            }
        }

        return null;
    }

    private static GeoPosition? FixPosition(AtcSession session, string fix)
    {
        foreach (RouteLeg leg in session.FlightPlan?.RouteLegs ?? [])
        {
            if (string.Equals(leg.Identifier, fix, StringComparison.OrdinalIgnoreCase) && leg.Position is GeoPosition position)
            {
                return position;
            }
        }

        return null;
    }

    /// <summary>The controller turns the flight away from a hazard it would enter, and releases it once past.</summary>
    private static AtcDecision? EvaluateHazards(AtcEvaluationContext context, Callsign callsign)
    {
        if (context.Hazards.Count == 0)
        {
            return null;
        }

        AtcSession session = context.Session;
        Aircraft.AircraftState aircraft = context.AircraftState;
        double variation = aircraft.MagneticHeadingDegrees - aircraft.TrueHeadingDegrees;
        Heading track = Heading.FromDegrees(aircraft.TrueHeadingDegrees);
        GeoPosition ahead = aircraft.Position.Project(track, HazardLookaheadMiles);

        foreach (HazardArea hazard in context.Hazards)
        {
            if (!hazard.IsActive || !hazard.CoversAltitude(aircraft.IndicatedAltitudeFeet))
            {
                continue;
            }

            string avoidKey = $"hazard-{hazard.Name}";
            string clearKey = $"hazard-clear-{hazard.Name}";
            double distance = aircraft.Position.DistanceNauticalMilesTo(hazard.Center);
            double relative = RelativeBearing(track, aircraft.Position.TrueBearingTo(hazard.Center));

            if (!session.IssuedInstructions.Contains(avoidKey))
            {
                if (hazard.CrossesPath(aircraft.Position, ahead, HazardMarginMiles))
                {
                    return HazardTurn(context, callsign, hazard, avoidKey, distance, relative, variation);
                }
            }
            else if (!session.IssuedInstructions.Contains(clearKey)
                && distance > hazard.RadiusNauticalMiles + HazardMarginMiles
                && (Math.Abs(relative) > 90 || distance > hazard.RadiusNauticalMiles + HazardLookaheadMiles))
            {
                return new AtcDecision
                {
                    Accepted = true,
                    ResponseType = AtcResponseType.Acknowledgement,
                    NewIssuedInstruction = clearKey,
                    ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["instruction"] = "resume own navigation" },
                    ProhibitedFacts = ["any clearance", "any altitude assignment"],
                    FallbackPhraseology = Say(context).HazardCleared(callsign, hazard),
                };
            }
        }

        return null;
    }

    private static AtcDecision HazardTurn(
        AtcEvaluationContext context,
        Callsign callsign,
        HazardArea hazard,
        string key,
        double distance,
        double relative,
        double variation)
    {
        Aircraft.AircraftState aircraft = context.AircraftState;
        bool turnRight = relative <= 0;
        Heading bearing = aircraft.Position.TrueBearingTo(hazard.Center);

        // Pass the area on the side the centre is not: its bearing plus the angle it subtends, and a margin.
        double spread = distance > hazard.RadiusNauticalMiles + HazardMarginMiles
            ? Math.Asin((hazard.RadiusNauticalMiles + HazardMarginMiles) / distance) * 180 / Math.PI
            : 90;
        double turn = Math.Clamp(spread + 8, 20, 90);
        var heading = Heading.FromDegrees(bearing.Degrees + (turnRight ? turn : -turn) + variation);

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Heading,
            IssuedBy = context.Session.CurrentController,
            IssuedAt = context.CurrentTime,
            AssignedHeading = heading,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.HeadingInstruction,
            NewIssuedInstruction = key,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.Heading,
                IssuedAt = context.CurrentTime,
                IsMandatory = false,
                ExpectedItems = [Expect(ReadbackItemType.Heading, heading.ToComparisonValue(), Say(context).ReadbackItemHeading(heading))],
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["heading"] = heading.ToComparisonValue(),
                ["reason"] = hazard.Name,
            },
            ProhibitedFacts = ["any clearance", "any altitude assignment"],
            FallbackPhraseology = Say(context).HazardAvoidance(callsign, heading, turnRight, hazard),
        };
    }

    /// <summary>The bearing of a target relative to a track, in degrees: positive to the right, negative to the left.</summary>
    private static double RelativeBearing(Heading track, Heading bearing)
        => (((bearing.Degrees - track.Degrees + 540) % 360) + 360) % 360 - 180;

    /// <summary>
    /// The number in the radar sequence for the approach: one more than the arrivals already closer to the destination.
    /// Null when the flight is first.
    /// </summary>
    private static int? RadarSequenceNumber(AtcEvaluationContext context, AirportData destination, double milesToDestination)
    {
        int ahead = 0;
        foreach (TrafficContact contact in context.Traffic)
        {
            double contactMiles = contact.Position.DistanceNauticalMilesTo(destination.ReferencePosition);
            if (!contact.IsOnGround
                && contact.AltitudeFeet <= destination.ElevationFeet + 10_000
                && contactMiles <= SequenceRadiusNauticalMiles
                && contactMiles < milesToDestination)
            {
                ahead++;
            }
        }

        return ahead == 0 ? null : Math.Min(ahead, 4) + 1;
    }

    /// <summary>"Demande un décalage de deux nautiques à droite": approved in the cruise, up to twenty miles.</summary>
    private static AtcDecision HandleOffset(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (RequiresAirborne(context, callsign, "An offset is asked for in flight.") is { } refused)
        {
            return refused;
        }

        int miles = OffsetExtractor.FindMiles(intent.SourceTranscript) ?? OffsetExtractor.DefaultMiles;
        if (miles is < 1 or > MaximumOffsetMiles)
        {
            return AtcDecision.Reject($"An offset of {miles} miles is outside 1 to {MaximumOffsetMiles}.", Say(context).OffsetRefused(callsign));
        }

        if (!context.Session.FlightRules.IsInstrument() || context.Session.Phase is not (FlightPhase.Enroute or FlightPhase.Departure))
        {
            return AtcDecision.Reject(
                "An offset is approved to an instrument flight in the cruise.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        AtcDecision approved = Acknowledge(
            context,
            callsign,
            Say(context).OffsetApproved(callsign, miles, OffsetExtractor.IsRight(intent.SourceTranscript)),
            $"offset {miles} miles");
        return approved with { NewOffsetMiles = miles };
    }

    private static AtcDecision HandleBackOnRoute(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.Session.OffsetMiles is null)
        {
            return Acknowledge(context, callsign, Say(context).Acknowledgement(callsign), "back on route");
        }

        AtcDecision ended = Acknowledge(context, callsign, Say(context).OffsetEnded(callsign), "offset cancelled");
        return ended with { NewOffsetMiles = 0 };
    }

    private static AtcDecision HandleCpdlcFailure(AtcEvaluationContext context) =>
        context.Session.Callsign is Callsign callsign
            ? Acknowledge(context, callsign, Say(context).CpdlcFailureAcknowledgement(callsign), "CPDLC failure, continue on voice")
            : AtcDecision.Silent();
}
