using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// VFR at a controlled aerodrome: the arrival call, reporting points, the legs of the circuit,
// touch and go, and leaving the frequency. A VFR aircraft needs no IFR clearance and no handoff
// to a departure controller: it stays with Tower for the whole circuit.
public sealed partial class StormAtcStateEngine
{
    /// <summary>
    /// "Lognes Tour, F-GABC, VFR from Melun for a touch and go, estimate Echo at 05": the first
    /// call of an arriving VFR aircraft.
    /// </summary>
    private static AtcDecision HandleVfrArrivalCall(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "An arrival call needs an airborne aircraft.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        // A military arrival is not given a circuit: it reports the initial point and breaks.
        if (IsMilitaryCall(context, intent))
        {
            int? qfe = QfeFor(context, context.DestinationAirport ?? context.DepartureAirport);
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.ReportPointInstruction,
                NewPhase = FlightPhase.Arrival,
                NewFlightRules = VisualRules(context),
                NewCircuitLeg = CircuitLeg.Joining,
                NewTouchAndGoRequested = intent.Entities.TouchAndGo,
                NewManoeuvre = intent.Entities.Manoeuvre == CircuitManoeuvre.None ? null : intent.Entities.Manoeuvre,
                NewArrivalRunway = runway,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = "report the initial point",
                    ["runway"] = runway.ToComparisonValue(),
                },
                ProhibitedFacts = ["landing clearance"],
                FallbackPhraseology = Say(context).MilitaryArrivalInstruction(
                    callsign, runway, context.Weather?.WindDirection, context.Weather?.WindSpeedKnots, qfe),
            };
        }

        // When the aerodrome publishes a reporting point, ask for a report there; the circuit
        // instruction follows. Without one, join the circuit straight away.
        VfrReportingPoint? point = context.VfrProfile?.FindPoint(intent.Entities.Waypoint)
            ?? (context.VfrProfile is { ReportingPoints.Count: > 0 } profile ? profile.ReportingPoints[0] : null);

        if (point is not null)
        {
            string spoken = session.Language == PhraseologyLanguage.French ? point.SpokenFrench : point.SpokenEnglish;
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.ReportPointInstruction,
                NewPhase = FlightPhase.Arrival,
                NewFlightRules = VisualRules(context),
                NewTouchAndGoRequested = intent.Entities.TouchAndGo,
                NewManoeuvre = intent.Entities.Manoeuvre == CircuitManoeuvre.None ? null : intent.Entities.Manoeuvre,
                NewArrivalRunway = runway,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = "report at the reporting point",
                    ["reportingPoint"] = point.Name,
                    ["runway"] = runway.ToComparisonValue(),
                },
                ProhibitedFacts = ["landing clearance", "any reporting point other than the approved one"],
                FallbackPhraseology = Say(context).ReportPointInstruction(callsign, spoken),
            };
        }

        return CircuitJoinDecision(context, callsign, runway, intent.Entities.TouchAndGo, intent.Entities.Manoeuvre);
    }

    /// <summary>"Passing Echo": the aircraft is at the reporting point, so it may join the circuit.</summary>
    private static AtcDecision HandleReportAtPoint(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "A reporting point is reported in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        return CircuitJoinDecision(context, callsign, runway, intent.Entities.TouchAndGo ? true : null, intent.Entities.Manoeuvre);
    }

    private static AtcDecision CircuitJoinDecision(
        AtcEvaluationContext context,
        Callsign callsign,
        RunwayIdentifier runway,
        bool? touchAndGo,
        CircuitManoeuvre manoeuvre = CircuitManoeuvre.None)
    {
        CircuitSide? side = context.VfrProfile?.CircuitSideFor(runway.CenterlineHeading.Degrees);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.CircuitJoinInstruction,
            NewPhase = FlightPhase.Circuit,
            NewFlightRules = VisualRules(context),
            NewCircuitLeg = CircuitLeg.Joining,
            NewTouchAndGoRequested = touchAndGo,
            NewManoeuvre = manoeuvre == CircuitManoeuvre.None ? null : manoeuvre,
            NewArrivalRunway = runway,
            ApprovedFacts = CircuitFacts(runway, side, "join the downwind leg and report downwind"),
            ProhibitedFacts = ["landing clearance", "any runway other than the runway in use"],
            FallbackPhraseology = Say(context).CircuitJoinInstruction(callsign, runway, side),
        };
    }

    /// <summary>"Downwind": ask for the base leg. "Base": ask for final.</summary>
    private static AtcDecision HandleCircuitLegReport(AtcEvaluationContext context, PilotIntent intent, CircuitLeg reported)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "A circuit leg is reported in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (session.CurrentController != ControllerPosition.Tower)
        {
            return AtcDecision.Reject(
                "The circuit is worked by Tower.",
                Say(context).Unable(callsign, UnableReason.ContactTower));
        }

        // The controller's runway, not the one the pilot named: a pilot reporting the wrong
        // runway is quietly given the right one in the next instruction.
        if (ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        CircuitSide? side = context.VfrProfile?.CircuitSideFor(runway.CenterlineHeading.Degrees);
        CircuitLeg next = reported == CircuitLeg.Downwind ? CircuitLeg.Base : CircuitLeg.Final;

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.CircuitReportInstruction,
            NewPhase = FlightPhase.Circuit,
            NewFlightRules = VisualRules(context),
            NewCircuitLeg = reported,
            NewTouchAndGoRequested = intent.Entities.TouchAndGo ? true : null,
            NewArrivalRunway = runway,
            ApprovedFacts = CircuitFacts(
                runway,
                side,
                next == CircuitLeg.Final ? "report final" : "report base"),
            ProhibitedFacts = ["landing clearance", "any runway other than the runway in use"],
            FallbackPhraseology = Say(context).CircuitReportInstruction(callsign, next, runway, side),
        };
    }

    /// <summary>"Passing initial, QFE 1005": QFE check, number one, report the last turn with the gear down.</summary>
    private static AtcDecision HandleInitialReport(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "The initial point is reported in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (session.CurrentController != ControllerPosition.Tower)
        {
            return AtcDecision.Reject(
                "The arrival is worked by Tower.",
                Say(context).Unable(callsign, UnableReason.ContactTower));
        }

        if (ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        // The QFE is corrected only when the pilot quoted one and it is not the one computed from the weather.
        int? actual = QfeFor(context, context.DestinationAirport ?? context.DepartureAirport);
        int? corrected = intent.Entities.QfeHectopascals is int said && actual is int expected && Math.Abs(said - expected) > 1
            ? expected
            : null;

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.CircuitReportInstruction,
            NewPhase = FlightPhase.Circuit,
            NewFlightRules = VisualRules(context),
            NewCircuitLeg = CircuitLeg.Base,
            NewArrivalRunway = runway,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = "report turning final, gear down and locked",
                ["runway"] = runway.ToComparisonValue(),
            },
            ProhibitedFacts = ["landing clearance"],
            FallbackPhraseology = Say(context).InitialPointInstruction(callsign, corrected),
        };
    }

    /// <summary>True for a flight in a CAM regime, or a call that names one.</summary>
    private static bool IsMilitaryCall(AtcEvaluationContext context, PilotIntent intent) =>
        context.Session.FlightRules.IsMilitary() || intent.Entities.DeclaredRegime is FlightRules regime && regime.IsMilitary();

    /// <summary>
    /// The QFE at an aerodrome: the QNH less the weight of the air between sea level and the field
    /// (about 1 hPa for 27 ft). Null when there is no weather source or no elevation: it is never invented.
    /// </summary>
    private static int? QfeFor(AtcEvaluationContext context, AirportData? airport) =>
        context.Weather?.QnhHectopascals is int qnh && airport is not null
            ? (int)Math.Round(qnh - (airport.ElevationFeet / 27.3), MidpointRounding.AwayFromZero)
            : null;

    /// <summary>A military taxi clearance gives the QFE.</summary>
    private static AtcDecision WithQfe(AtcDecision decision, AtcEvaluationContext context, PilotIntent intent)
    {
        if (!decision.Accepted || decision.ResponseType != AtcResponseType.TaxiClearance || !IsMilitaryCall(context, intent)
            || QfeFor(context, context.DepartureAirport) is not int qfe)
        {
            return decision;
        }

        return decision with { FallbackPhraseology = $"{decision.FallbackPhraseology.TrimEnd('.')}, {Say(context).Qfe(qfe)}." };
    }

    /// <summary>"Final": cleared to land, or cleared for a touch and go when that was asked.</summary>
    private static AtcDecision HandleFinalReport(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "Final is reported in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (session.AssignedArrivalRunway is null && ResolveVfrRunway(context) is RunwayIdentifier chosen)
        {
            session = session.WithArrivalRunway(chosen);
            context = context with { Session = session };
        }

        // A military pilot reports the last turn with the gear down and locked; without it the controller asks.
        if (IsMilitaryCall(context, intent) && !intent.Entities.GearDownAndLocked && session.RadarApproach is not { GearChecked: true })
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                NewPhase = FlightPhase.Circuit,
                NewCircuitLeg = CircuitLeg.Final,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = "check gear down and locked",
                },
                ProhibitedFacts = ["landing clearance before the gear is reported down and locked"],
                FallbackPhraseology = Say(context).CheckGearDownAndLocked(callsign),
            };
        }

        bool touchAndGo = intent.Entities.TouchAndGo || session.TouchAndGoRequested;
        CircuitManoeuvre manoeuvre = intent.Entities.Manoeuvre != CircuitManoeuvre.None
            ? intent.Entities.Manoeuvre
            : session.RequestedManoeuvre;
        return HandleLandingClearance(context, callsign, touchAndGo, manoeuvre) with
        {
            NewFlightRules = VisualRules(context),
            NewCircuitLeg = CircuitLeg.Final,
            EndsRadarApproach = session.RadarApproach is not null,
            NewTouchAndGoRequested = touchAndGo,
            NewManoeuvre = manoeuvre,
        };
    }

    /// <summary>"Request touch and go": noted; the clearance itself comes when the aircraft reports final.</summary>
    private static AtcDecision HandleTouchAndGoRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewTouchAndGoRequested = intent.Entities.Manoeuvre == CircuitManoeuvre.None ? true : null,
            NewManoeuvre = intent.Entities.Manoeuvre == CircuitManoeuvre.None ? null : intent.Entities.Manoeuvre,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = intent.Entities.Manoeuvre switch
                {
                    CircuitManoeuvre.Option => "option requested",
                    CircuitManoeuvre.LowPass => "low pass requested",
                    _ => "touch and go requested",
                },
            },
            ProhibitedFacts = ["landing clearance", "any clearance before the aircraft reports final"],
            FallbackPhraseology = Say(context).Acknowledgement(callsign),
        };
    }

    /// <summary>"Leaving the circuit and the frequency."</summary>
    private static AtcDecision HandleLeaveFrequency(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewPhase = FlightPhase.Enroute,
            NewCircuitLeg = CircuitLeg.None,
            NewTouchAndGoRequested = false,
            NewManoeuvre = CircuitManoeuvre.None,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = "leaving the frequency",
            },
            ProhibitedFacts = ["any new clearance"],
            FallbackPhraseology = Say(context).LeaveFrequencyAcknowledgement(callsign),
        };
    }

    /// <summary>A visual flight keeps its regime: CAM V stays CAM V, anything else visual is VFR.</summary>
    private static FlightRules VisualRules(AtcEvaluationContext context) =>
        context.Session.FlightRules.IsVisual() ? context.Session.FlightRules : FlightRules.Vfr;

    /// <summary>
    /// Marks a decision as VFR when the pilot said so ("local flight", "VFR"), so a VFR flight
    /// is recognised from its first transmission.
    /// </summary>
    private static AtcDecision DeclareRules(AtcDecision decision, PilotIntent intent, AtcEvaluationContext context) =>
        decision.Accepted && decision.NewFlightRules is null && intent.Entities.DeclaredRegime is FlightRules regime
            ? decision with { NewFlightRules = regime }
            : decision.Accepted && intent.Entities.DeclaresVfr && decision.NewFlightRules is null
                ? decision with { NewFlightRules = VisualRules(context) }
                : decision;

    /// <summary>The pilot asks to change military regime: CAM Victor (à vue) or CAM India (instruments).</summary>
    private static AtcDecision HandleRegimeChange(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (intent.Entities.DeclaredRegime is not FlightRules regime)
        {
            return HandleUnrecognized(context, intent);
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewFlightRules = regime,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["regime"] = regime.ToLabel(),
            },
            FallbackPhraseology = Say(context).RegimeChange(callsign, regime.ToSpokenLabel()),
        };
    }

    /// <summary>
    /// The runway in use for a VFR circuit or arrival: the one already assigned in this circuit,
    /// the runway the flight departed from when it is a local flight, then the one the wind
    /// favours, and finally the aerodrome's preferred direction.
    /// </summary>
    private static RunwayIdentifier? ResolveVfrRunway(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.CircuitLeg != CircuitLeg.None && session.AssignedArrivalRunway is RunwayIdentifier inUse)
        {
            return inUse;
        }

        bool localFlight = session.FlightPlan is { } plan
            && string.Equals(plan.DepartureIcao, plan.DestinationIcao, StringComparison.OrdinalIgnoreCase);
        if (localFlight && session.AssignedDepartureRunway is RunwayIdentifier departedFrom)
        {
            return departedFrom;
        }

        AirportData? airport = context.RelevantAirport;
        if (airport is null || airport.Runways.Count == 0)
        {
            return session.AssignedArrivalRunway ?? session.FlightPlan?.ArrivalRunway;
        }

        if (context.Weather?.WindDirection is null && context.VfrProfile?.PreferredQfu is int preferred)
        {
            Heading target = Heading.FromDegrees(preferred);
            return airport.Runways
                .OrderBy(runway => runway.Identifier.CenterlineHeading.DifferenceTo(target))
                .First()
                .Identifier;
        }

        return airport.SelectActiveRunway(context.Weather?.WindDirection)?.Identifier;
    }

    private static Dictionary<string, string> CircuitFacts(RunwayIdentifier runway, CircuitSide? side, string instruction)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runway"] = runway.ToComparisonValue(),
            ["instruction"] = instruction,
        };

        if (side is CircuitSide hand)
        {
            facts["circuit"] = hand == CircuitSide.Left ? "left-hand" : "right-hand";
        }

        return facts;
    }
}
