using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>The everyday procedures of the phraseology manual: what the engine answers.</summary>
public class ProceduresEngineTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    private static AtcSession OnGround(FlightPhase phase = FlightPhase.TaxiOut) => TestScenario.NewSession(TestScenario.AtHoldingPoint()) with
    {
        CurrentController = ControllerPosition.Ground,
        CurrentFrequency = TestScenario.Ground,
        Phase = phase,
    };

    private static AtcSession InTheAir(FlightRules rules = FlightRules.Vfr) => TestScenario.NewSession(TestScenario.Airborne(2500)) with
    {
        FlightRules = rules,
        CurrentController = ControllerPosition.Tower,
        CurrentFrequency = TestScenario.Tower,
        Phase = FlightPhase.Enroute,
    };

    private static PilotIntent Intent(PilotIntentType type, IntentEntities? entities = null) =>
        TestScenario.Intent(type, "request", entities: entities);

    private AtcDecision Run(AtcSession session, PilotIntentType type, IntentEntities? entities = null, WeatherSnapshot? weather = null) =>
        _engine.Evaluate(TestScenario.Context(session, Intent(type, entities)) with { Weather = weather });

    [Fact]
    public void A_backtrack_gives_the_runway_and_asks_to_report_ready()
    {
        AtcDecision decision = Run(OnGround(), PilotIntentType.RequestBacktrack);

        Assert.True(decision.Accepted);
        Assert.Contains("backtrack runway", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("report ready", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backtrack_is_refused_in_the_air()
    {
        Assert.False(Run(InTheAir(), PilotIntentType.RequestBacktrack).Accepted);
    }

    [Fact]
    public void The_time_is_given_in_utc()
    {
        AtcDecision decision = Run(OnGround(), PilotIntentType.RequestTimeCheck);

        Assert.Contains("time one two zero zero UTC", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ident_is_identified_in_the_air_and_only_acknowledged_on_the_ground()
    {
        Assert.Contains("identified", Run(InTheAir(), PilotIntentType.ReportIdent).FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("roger", Run(OnGround(), PilotIntentType.ReportIdent).FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void Speaking_slower_repeats_the_last_transmission()
    {
        AtcSession session = InTheAir().WithConversationTurn(new ConversationTurn
        {
            TurnId = Guid.CreateVersion7(),
            Timestamp = TestScenario.Now,
            Speaker = ConversationSpeaker.Controller,
            Controller = ControllerPosition.Tower,
            Text = "Storm one two three, contact Departure one one nine decimal one.",
        });

        AtcDecision decision = Run(session, PilotIntentType.SpeakSlowly);

        Assert.StartsWith("I say again, slowly:", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("contact Departure", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_ifr_makes_the_flight_vfr_and_only_in_the_air()
    {
        AtcDecision decision = Run(InTheAir(FlightRules.Ifr), PilotIntentType.CancelIfr);

        Assert.Equal(FlightRules.Vfr, decision.NewFlightRules);
        Assert.Contains("IFR flight plan cancelled", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.False(Run(InTheAir(FlightRules.Vfr), PilotIntentType.CancelIfr).Accepted);
        Assert.False(Run(OnGround(), PilotIntentType.CancelIfr).Accepted);
    }

    [Fact]
    public void Special_vfr_is_given_unless_the_visibility_is_too_low()
    {
        Assert.True(Run(InTheAir(), PilotIntentType.RequestSpecialVfr, weather: new WeatherSnapshot { VisibilityMeters = 3000 }).Accepted);

        AtcDecision refused = Run(InTheAir(), PilotIntentType.RequestSpecialVfr, weather: new WeatherSnapshot { VisibilityMeters = 1200 });
        Assert.False(refused.Accepted);
        Assert.Contains("1200 metres", refused.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transit_is_cleared_at_an_altitude_and_overhead_the_aerodrome_nothing_is_joined()
    {
        AtcDecision clearance = Run(InTheAir(), PilotIntentType.RequestTransit, weather: new WeatherSnapshot { QnhHectopascals = 1015 });

        Assert.True(clearance.Accepted);
        Assert.True(clearance.NewVfrTransit);
        Assert.Contains("transit overhead the aerodrome", clearance.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("QNH", clearance.FallbackPhraseology, StringComparison.Ordinal);

        AtcDecision overhead = Run(InTheAir() with { VfrTransit = true }, PilotIntentType.ReportAtPoint);

        Assert.False(overhead.NewVfrTransit);
        Assert.Null(overhead.NewCircuitLeg);
        Assert.Contains("report leaving the zone", overhead.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_orbit_an_extended_downwind_and_a_closed_flight_plan_are_acknowledged()
    {
        Assert.Contains(
            "right 360 approved",
            Run(InTheAir(), PilotIntentType.RequestOrbit, new IntentEntities { CircuitSide = CircuitSide.Right }).FallbackPhraseology,
            StringComparison.Ordinal);
        Assert.Contains("extend downwind", Run(InTheAir(), PilotIntentType.RequestExtendDownwind).FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("flight plan closed", Run(OnGround(FlightPhase.Parked), PilotIntentType.RequestFlightPlanClosure).FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("roger", Run(InTheAir(), PilotIntentType.ReportTrafficLookout).FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_immediate_departure_is_cleared_for_an_immediate_takeoff()
    {
        AtcSession session = TestScenario.NewSession(TestScenario.AtHoldingPoint()) with
        {
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = TestScenario.Tower,
            Phase = FlightPhase.HoldingShort,
        };
        PilotIntent ready = TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready immediate", entities: new IntentEntities { ReadyImmediate = true });

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, ready));

        Assert.Equal(AtcResponseType.TakeoffClearance, decision.ResponseType);
        Assert.Contains("immediate takeoff", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    private static AtcSession FollowedInTheAir() => InTheAir() with
    {
        CurrentController = ControllerPosition.Information,
        CurrentFrequency = StormATC.Domain.Airports.FlightInformationRegions.ParisSud.Frequency,
        FlightFollowing = new StormATC.Domain.Approach.FlightFollowingState
        {
            ServiceName = "Paris Information",
            Frequency = StormATC.Domain.Airports.FlightInformationRegions.ParisSud.Frequency,
            Squawk = SquawkCode.Create("2345"),
            Since = TestScenario.Now,
        },
    };

    [Fact]
    public void Traffic_close_to_a_followed_flight_is_called_with_its_clock_position_and_altitude()
    {
        AtcSession session = FollowedInTheAir();
        // 3 nm dead ahead of the aircraft, 500 ft above.
        GeoPosition ahead = session.AircraftState.Position.Project(Heading.FromDegrees(session.AircraftState.TrueHeadingDegrees), 3);
        var other = new TrafficContact { Callsign = "OTHER", Position = ahead, AltitudeFeet = 3000, GroundSpeedKnots = 100 };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null, traffic: [other]));

        Assert.NotNull(decision.TrafficAdvisedAt);
        Assert.Contains("traffic 12 o'clock, 3 miles, 500 feet above", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void Traffic_is_not_called_again_within_three_minutes_nor_when_it_is_far_or_on_the_ground()
    {
        AtcSession session = FollowedInTheAir();
        GeoPosition near = session.AircraftState.Position.Project(Heading.FromDegrees(session.AircraftState.TrueHeadingDegrees), 3);

        AtcDecision again = _engine.EvaluateStateChange(TestScenario.Context(
            session.WithTrafficAdvisedAt(TestScenario.Now - TimeSpan.FromMinutes(1)),
            null,
            traffic: [new TrafficContact { Callsign = "OTHER", Position = near, AltitudeFeet = 2600 }]));
        Assert.Null(again.TrafficAdvisedAt);

        AtcDecision onGround = _engine.EvaluateStateChange(TestScenario.Context(
            session, null, traffic: [new TrafficContact { Callsign = "OTHER", Position = near, AltitudeFeet = 2600, IsOnGround = true }]));
        Assert.Null(onGround.TrafficAdvisedAt);

        GeoPosition far = session.AircraftState.Position.Project(Heading.FromDegrees(0), 20);
        AtcDecision tooFar = _engine.EvaluateStateChange(TestScenario.Context(
            session, null, traffic: [new TrafficContact { Callsign = "OTHER", Position = far, AltitudeFeet = 2600 }]));
        Assert.Null(tooFar.TrafficAdvisedAt);
    }
}
