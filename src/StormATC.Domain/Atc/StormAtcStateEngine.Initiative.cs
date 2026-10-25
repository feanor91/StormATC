using StormATC.Domain.Airports;
using StormATC.Domain.Approach;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// What a radar controller orders by himself to an IFR flight, without being asked: a speed on the way in, a hold when the
// arrival is busy, a direct to shorten the route. Each is read back, and each is given once in a flight.
public sealed partial class StormAtcStateEngine
{
    /// <summary>How long a hold the controller ordered lasts before he releases the flight.</summary>
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(3);

    /// <summary>The distance to the destination inside which the first speed reduction is given, in nautical miles.</summary>
    private const double FirstSpeedReductionMiles = 35;

    /// <summary>The distance to the destination inside which the second speed reduction is given, in nautical miles.</summary>
    private const double SecondSpeedReductionMiles = 18;

    private static AtcDecision? EvaluateInstructions(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        Aircraft.AircraftState aircraft = context.AircraftState;

        if (aircraft.IsOnGround
            || session.Callsign is not Callsign callsign
            || !session.FlightRules.IsInstrument()
            || session.CurrentController is not (ControllerPosition.Center or ControllerPosition.Approach
                or ControllerPosition.Departure))
        {
            return null;
        }

        // A hold ordered earlier: after a few minutes the traffic has gone, the flight is released.
        if (session.Hold is HoldState hold)
        {
            return context.CurrentTime - hold.Since >= HoldDuration
                ? new AtcDecision
                {
                    Accepted = true,
                    ResponseType = AtcResponseType.Acknowledgement,
                    EndsHold = true,
                    ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["instruction"] = "leave the hold" },
                    ProhibitedFacts = ["any altitude change"],
                    FallbackPhraseology = Say(context).LeaveHoldInstruction(callsign),
                }
                : null;
        }

        if (context.DestinationAirport is not AirportData destination)
        {
            return null;
        }

        double miles = aircraft.Position.DistanceNauticalMilesTo(destination.ReferencePosition);
        double indicated = aircraft.IndicatedAirspeedKnots;

        // --- Speed on the way in. -----------------------------------------------------------------------
        if (miles <= SecondSpeedReductionMiles && indicated > 215 && !session.IssuedInstructions.Contains("speed-180"))
        {
            return SpeedOrder(context, callsign, 180, "speed-180");
        }

        if (miles <= FirstSpeedReductionMiles && miles > SecondSpeedReductionMiles && indicated > 250
            && !session.IssuedInstructions.Contains("speed-220"))
        {
            return SpeedOrder(context, callsign, 220, "speed-220");
        }

        // --- A hold when the arrival is busy. ------------------------------------------------------------
        if (session.CurrentController == ControllerPosition.Approach
            && miles <= 30
            && !session.IssuedInstructions.Contains("hold")
            && CircuitTrafficCount(context) >= 2)
        {
            string? fix = FixNear(context, destination, 40);
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                NewHold = new HoldState { Fix = fix, Since = context.CurrentTime },
                NewIssuedInstruction = "hold",
                RequiredReadback = ReadbackOf(
                    context,
                    ClearanceType.HoldInstruction,
                    fix is null ? [] : [Expect(ReadbackItemType.HoldFix, fix, Say(context).ReadbackItemHold(fix))]),
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = fix is null ? "hold at present position" : $"hold over {fix}",
                },
                ProhibitedFacts = ["any clearance", "any altitude assignment"],
                FallbackPhraseology = Say(context).HoldInstruction(callsign, fix),
            };
        }

        // --- A direct that shortens the route. -----------------------------------------------------------
        if (session.CurrentController == ControllerPosition.Center
            && session.Phase == FlightPhase.Enroute
            && miles > 60
            && !session.IssuedInstructions.Contains("direct")
            && DirectFix(session, aircraft, destination, miles) is string direct)
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.DirectToApproval,
                NewIssuedInstruction = "direct",
                IssuedClearance = new Clearance
                {
                    ClearanceId = Guid.CreateVersion7(),
                    Type = ClearanceType.DirectTo,
                    IssuedBy = session.CurrentController,
                    IssuedAt = context.CurrentTime,
                    ClearedRoute = $"DCT {direct}",
                },
                RequiredReadback = ReadbackOf(
                    context,
                    ClearanceType.DirectTo,
                    [Expect(ReadbackItemType.RouteFix, direct, Say(context).ReadbackItemDirect(direct))]),
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["directTo"] = direct },
                ProhibitedFacts = ["any waypoint other than the approved one", "altitude change"],
                FallbackPhraseology = Say(context).DirectToApproval(callsign, direct),
            };
        }

        return null;
    }

    private static AtcDecision SpeedOrder(AtcEvaluationContext context, Callsign callsign, int knots, string key) => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.Acknowledgement,
        NewAssignedSpeedKnots = knots,
        NewIssuedInstruction = key,
        RequiredReadback = ReadbackOf(
            context,
            ClearanceType.SpeedInstruction,
            [Expect(ReadbackItemType.Speed, knots.ToString(System.Globalization.CultureInfo.InvariantCulture), Say(context).ReadbackItemSpeed(knots))]),
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["speed"] = knots.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        ProhibitedFacts = ["any clearance", "any altitude assignment"],
        FallbackPhraseology = Say(context).SpeedReduction(callsign, knots),
    };

    private static ExpectedReadbackItem Expect(ReadbackItemType type, string value, string spoken) => new()
    {
        Type = type,
        ExpectedValue = value,
        SpokenValue = spoken,
        IsCritical = true,
    };

    /// <summary>
    /// A readback of an instruction the controller gave himself. It is expected but never blocks the flight: a pilot
    /// who does not read back is reminded only when he reads back wrongly.
    /// </summary>
    private static PendingReadback ReadbackOf(AtcEvaluationContext context, ClearanceType type, IReadOnlyList<ExpectedReadbackItem> items) => new()
    {
        ClearanceId = Guid.CreateVersion7(),
        ClearanceType = type,
        ExpectedItems = items,
        IssuedAt = context.CurrentTime,
        IsMandatory = false,
    };

    /// <summary>The fix of the route nearest to the destination within a distance of it, or null.</summary>
    private static string? FixNear(AtcEvaluationContext context, AirportData destination, double withinMiles)
    {
        string? best = null;
        double bestMiles = double.MaxValue;
        foreach (RouteLeg leg in context.Session.FlightPlan?.RouteLegs ?? [])
        {
            if (leg.Position is not GeoPosition position || !IsFixName(leg.Identifier))
            {
                continue;
            }

            double miles = position.DistanceNauticalMilesTo(destination.ReferencePosition);
            if (miles <= withinMiles && miles < bestMiles)
            {
                best = leg.Identifier.ToUpperInvariant();
                bestMiles = miles;
            }
        }

        return best;
    }

    /// <summary>A fix ahead of the aircraft, closer to the destination, far enough to be worth a direct.</summary>
    private static string? DirectFix(AtcSession session, Aircraft.AircraftState aircraft, AirportData destination, double milesToDestination)
    {
        string? best = null;
        double bestMiles = double.MaxValue;
        foreach (RouteLeg leg in session.FlightPlan?.RouteLegs ?? [])
        {
            if (leg.Position is not GeoPosition position || !IsFixName(leg.Identifier))
            {
                continue;
            }

            double fromDestination = position.DistanceNauticalMilesTo(destination.ReferencePosition);
            double fromAircraft = aircraft.Position.DistanceNauticalMilesTo(position);
            if (fromAircraft >= 25 && fromDestination < milesToDestination - 15 && fromDestination > 25 && fromDestination < bestMiles)
            {
                best = leg.Identifier.ToUpperInvariant();
                bestMiles = fromDestination;
            }
        }

        return best;
    }

    private static bool IsFixName(string identifier) =>
        identifier.Length is >= 3 and <= 5 && identifier.All(char.IsLetter);
}
