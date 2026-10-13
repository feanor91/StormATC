using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// A VFR flight at a controlled aerodrome: arrival call, reporting point, the legs of the
/// circuit, touch and go, and leaving the frequency. The aerodrome here is the fictional STMR
/// with a profile modelled on a real one: one reporting point, circuit east of the runways.
/// </summary>
public class VfrEngineTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    private static readonly VfrAerodromeProfile Profile = new()
    {
        Icao = "STMR",
        ReportingPoints = [new VfrReportingPoint("E", "Écho", "Echo")],
        CircuitHeightFeetAmsl = 1200,
        CircuitSideBearing = 90,
    };

    /// <summary>An airborne VFR aircraft on the Tower frequency, flying a local flight from STMR.</summary>
    private static AtcSession VfrSession(
        FlightPhase phase = FlightPhase.Arrival,
        CircuitLeg leg = CircuitLeg.None,
        PhraseologyLanguage language = PhraseologyLanguage.English,
        bool onGround = false) => TestScenario.NewSession(
            onGround ? TestScenario.AtHoldingPoint() : TestScenario.Airborne(1200))
        with
        {
            FlightPlan = TestScenario.SampleFlightPlan with { DestinationIcao = "STMR" },
            FlightRules = FlightRules.Vfr,
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = TestScenario.Tower,
            Phase = phase,
            CircuitLeg = leg,
            Language = language,
        };

    private static AtcEvaluationContext Context(AtcSession session, PilotIntent? intent, bool withProfile = true)
        => TestScenario.Context(session, intent) with
        {
            DestinationAirport = TestScenario.Departure_STMR,
            VfrProfile = withProfile ? Profile : null,
        };

    private static PilotIntent Intent(PilotIntentType type, IntentEntities? entities = null) =>
        TestScenario.Intent(type, "vfr transmission", entities: entities);

    // =====================================================================================
    // Arrival and the reporting point
    // =====================================================================================

    [Fact]
    public void An_arrival_call_asks_for_a_report_at_the_reporting_point_and_marks_the_flight_vfr()
    {
        AtcSession session = VfrSession() with { FlightRules = FlightRules.Ifr };

        AtcDecision decision = _engine.Evaluate(Context(session, Intent(
            PilotIntentType.VfrArrivalCall,
            new IntentEntities { DeclaresVfr = true, TouchAndGo = true, Waypoint = "E" })));

        Assert.True(decision.Accepted);
        Assert.Equal(AtcResponseType.ReportPointInstruction, decision.ResponseType);
        Assert.Equal(FlightRules.Vfr, decision.NewFlightRules);
        Assert.Equal(FlightPhase.Arrival, decision.NewPhase);
        Assert.True(decision.NewTouchAndGoRequested);
        Assert.Contains("report Echo", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void Without_a_profile_the_arrival_call_goes_straight_to_the_circuit()
    {
        AtcDecision decision = _engine.Evaluate(Context(
            VfrSession(),
            Intent(PilotIntentType.VfrArrivalCall, new IntentEntities { DeclaresVfr = true }),
            withProfile: false));

        Assert.Equal(AtcResponseType.CircuitJoinInstruction, decision.ResponseType);
        Assert.Equal(CircuitLeg.Joining, decision.NewCircuitLeg);
        Assert.Contains("join downwind", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.DoesNotContain("left-hand", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_arrival_call_from_the_ground_is_refused()
    {
        AtcDecision decision = _engine.Evaluate(Context(
            VfrSession(onGround: true),
            Intent(PilotIntentType.VfrArrivalCall)));

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void Passing_the_reporting_point_joins_the_circuit_with_the_hand_of_the_runway_in_use()
    {
        AtcSession session = VfrSession();

        AtcDecision decision = _engine.Evaluate(Context(session, Intent(PilotIntentType.ReportAtPoint)));

        Assert.Equal(AtcResponseType.CircuitJoinInstruction, decision.ResponseType);
        Assert.Equal(FlightPhase.Circuit, decision.NewPhase);
        Assert.Equal(CircuitLeg.Joining, decision.NewCircuitLeg);
        RunwayIdentifier runway = decision.NewArrivalRunway!.Value;

        // The circuit lies east of the runways: left-hand for 18 (landing south), right for 36.
        string expectedHand = runway.CenterlineHeading.Degrees == 180 ? "left-hand" : "right-hand";
        Assert.Contains(expectedHand + " downwind", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("report downwind", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    // =====================================================================================
    // The circuit
    // =====================================================================================

    [Fact]
    public void Downwind_is_answered_with_a_request_to_report_base_then_final()
    {
        AtcSession session = VfrSession(FlightPhase.Circuit, CircuitLeg.Joining) with
        {
            AssignedArrivalRunway = RunwayIdentifier.Create("18"),
        };

        AtcDecision downwind = _engine.Evaluate(Context(session, Intent(PilotIntentType.ReportDownwind)));
        Assert.Equal(CircuitLeg.Downwind, downwind.NewCircuitLeg);
        Assert.Contains("report left-hand base runway one eight", downwind.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession atBase = session with { CircuitLeg = CircuitLeg.Downwind };
        AtcDecision baseLeg = _engine.Evaluate(Context(atBase, Intent(PilotIntentType.ReportBase)));
        Assert.Equal(CircuitLeg.Base, baseLeg.NewCircuitLeg);
        Assert.Contains("report final runway one eight", baseLeg.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_to_the_wrong_controller_is_redirected()
    {
        AtcSession session = VfrSession(FlightPhase.Circuit, CircuitLeg.Joining) with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
        };

        AtcDecision decision = _engine.Evaluate(Context(session, Intent(PilotIntentType.ReportDownwind)));

        Assert.False(decision.Accepted);
        Assert.Contains("contact tower", decision.FallbackPhraseology, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // Final: landing or touch and go
    // =====================================================================================

    [Fact]
    public void Final_gets_a_landing_clearance_for_a_full_stop()
    {
        AtcSession session = VfrSession(FlightPhase.Circuit, CircuitLeg.Base) with
        {
            AssignedArrivalRunway = RunwayIdentifier.Create("18"),
        };

        AtcDecision decision = _engine.Evaluate(Context(session, Intent(PilotIntentType.ReportFinal)));

        Assert.Equal(AtcResponseType.LandingClearance, decision.ResponseType);
        Assert.Equal(ClearanceType.LandingClearance, decision.IssuedClearance?.Type);
        Assert.False(decision.IssuedClearance!.TouchAndGo);
        Assert.Contains("cleared to land", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Equal(CircuitLeg.Final, decision.NewCircuitLeg);
    }

    [Fact]
    public void Final_after_a_touch_and_go_request_gets_a_touch_and_go_clearance()
    {
        AtcSession session = VfrSession(FlightPhase.Circuit, CircuitLeg.Base) with
        {
            AssignedArrivalRunway = RunwayIdentifier.Create("18"),
            TouchAndGoRequested = true,
        };

        AtcDecision decision = _engine.Evaluate(Context(session, Intent(PilotIntentType.ReportFinal)));

        Assert.Equal(AtcResponseType.TouchAndGoClearance, decision.ResponseType);
        Assert.True(decision.IssuedClearance!.TouchAndGo);
        Assert.Contains("cleared touch and go", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.NotNull(decision.RequiredReadback);
    }

    [Fact]
    public void Final_with_traffic_on_the_runway_is_not_cleared()
    {
        AtcSession session = VfrSession(FlightPhase.Circuit, CircuitLeg.Base) with
        {
            AssignedArrivalRunway = RunwayIdentifier.Create("18"),
        };
        AtcEvaluationContext context = Context(session, Intent(PilotIntentType.ReportFinal)) with
        {
            Traffic =
            [
                new TrafficContact
                {
                    Callsign = "DLH1",
                    Position = TestScenario.Runway18Midpoint,
                    AltitudeFeet = 0,
                    IsOnRunway = true,
                    OccupiedRunway = RunwayIdentifier.Create("18"),
                },
            ],
        };

        AtcDecision decision = _engine.Evaluate(context);

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void A_touch_and_go_request_is_noted_and_the_clearance_waits_for_final()
    {
        AtcDecision decision = _engine.Evaluate(Context(
            VfrSession(FlightPhase.Circuit, CircuitLeg.Downwind),
            Intent(PilotIntentType.RequestTouchAndGo)));

        Assert.True(decision.Accepted);
        Assert.True(decision.NewTouchAndGoRequested);
        Assert.Null(decision.IssuedClearance);
    }

    // =====================================================================================
    // What the aircraft does next
    // =====================================================================================

    [Fact]
    public void Touching_down_on_a_touch_and_go_neither_vacates_nor_hands_off()
    {
        AtcSession session = VfrSession(FlightPhase.Final, CircuitLeg.Final) with
        {
            AircraftState = TestScenario.AtHoldingPoint(),
            ArrivalClearance = new Clearance
            {
                ClearanceId = Guid.CreateVersion7(),
                Type = ClearanceType.LandingClearance,
                IssuedBy = ControllerPosition.Tower,
                IssuedAt = TestScenario.Now,
                Runway = RunwayIdentifier.Create("18"),
                TouchAndGo = true,
            },
        };

        AtcDecision decision = _engine.EvaluateStateChange(Context(session, intent: null));

        Assert.Equal(AtcResponseType.None, decision.ResponseType);
        Assert.Equal(FlightPhase.Takeoff, decision.NewPhase);
        Assert.Null(decision.Handoff);
    }

    [Fact]
    public void Touching_down_on_a_full_stop_gives_the_vacate_instruction()
    {
        AtcSession session = VfrSession(FlightPhase.Final, CircuitLeg.Final) with
        {
            AircraftState = TestScenario.AtHoldingPoint(),
        };

        AtcDecision decision = _engine.EvaluateStateChange(Context(session, intent: null));

        Assert.Equal(AtcResponseType.VacateInstruction, decision.ResponseType);
        Assert.Equal(FlightPhase.Landed, decision.NewPhase);
    }

    [Theory]
    [InlineData(FlightRules.Ifr, true)]
    [InlineData(FlightRules.Vfr, false)]
    public void Only_an_ifr_departure_is_handed_to_departure(FlightRules rules, bool handedOff)
    {
        AtcSession session = VfrSession(FlightPhase.InitialClimb) with
        {
            FlightRules = rules,
            AircraftState = TestScenario.Airborne(1_500) with { VerticalSpeedFeetPerMinute = 1_500 },
            FlightPlan = TestScenario.SampleFlightPlan,
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.Equal(handedOff, decision.Handoff is not null);
    }

    [Fact]
    public void A_local_flight_taxi_request_marks_the_flight_vfr()
    {
        AtcSession session = TestScenario.NewSession() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, Intent(
            PilotIntentType.RequestTaxi,
            new IntentEntities { DeclaresVfr = true })));

        Assert.True(decision.Accepted);
        Assert.Equal(FlightRules.Vfr, decision.NewFlightRules);
        Assert.Equal(ClearanceType.Taxi, decision.IssuedClearance?.Type);
    }

    [Fact]
    public void Leaving_the_frequency_ends_the_circuit()
    {
        AtcDecision decision = _engine.Evaluate(Context(
            VfrSession(FlightPhase.Circuit, CircuitLeg.Downwind),
            Intent(PilotIntentType.LeaveFrequency)));

        Assert.True(decision.Accepted);
        Assert.Equal(FlightPhase.Enroute, decision.NewPhase);
        Assert.Equal(CircuitLeg.None, decision.NewCircuitLeg);
        Assert.Contains("good day", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    // =====================================================================================
    // In French
    // =====================================================================================

    [Fact]
    public void The_whole_circuit_is_spoken_in_french_with_the_dsna_wording()
    {
        AtcSession session = VfrSession(language: PhraseologyLanguage.French);
        AtcDecision arrival = _engine.Evaluate(Context(session, Intent(
            PilotIntentType.VfrArrivalCall, new IntentEntities { DeclaresVfr = true, Waypoint = "E" })));
        Assert.EndsWith("reçu, rappelez Écho.", arrival.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession atPoint = session with { Phase = FlightPhase.Arrival };
        AtcDecision join = _engine.Evaluate(Context(atPoint, Intent(PilotIntentType.ReportAtPoint)));
        string hand = join.NewArrivalRunway!.Value.CenterlineHeading.Degrees == 180 ? "main gauche" : "main droite";
        Assert.Contains($"entrez vent arrière {hand} piste", join.FallbackPhraseology, StringComparison.Ordinal);
        Assert.EndsWith("rappelez vent arrière.", join.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession downwind = VfrSession(FlightPhase.Circuit, CircuitLeg.Joining, PhraseologyLanguage.French) with
        {
            AssignedArrivalRunway = RunwayIdentifier.Create("18"),
        };
        AtcDecision baseCall = _engine.Evaluate(Context(downwind, Intent(PilotIntentType.ReportDownwind)));
        Assert.Contains("rappelez base main gauche piste", baseCall.FallbackPhraseology, StringComparison.Ordinal);

        AtcDecision touch = _engine.Evaluate(Context(
            downwind with { CircuitLeg = CircuitLeg.Base, TouchAndGoRequested = true },
            Intent(PilotIntentType.ReportFinal)));
        Assert.Contains("autorisé toucher", touch.FallbackPhraseology, StringComparison.Ordinal);
    }
}
