using StormATC.Domain.Airports;
using StormATC.Domain.Approach;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>What a radar controller orders by himself to an IFR flight: a speed, a hold, a direct.</summary>
public class ControllerInitiativeTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    private static AtcSession Session(
        ControllerPosition controller = ControllerPosition.Approach,
        double indicatedAirspeed = 280,
        FlightRules rules = FlightRules.Ifr,
        FlightPhase phase = FlightPhase.Descent) => TestScenario.NewSession(TestScenario.Airborne(6000) with { IndicatedAirspeedKnots = indicatedAirspeed })
        with
    {
        FlightRules = rules,
        CurrentController = controller,
        CurrentFrequency = TestScenario.Approach,
        Phase = phase,
    };

    private static AirportData DestinationAt(AtcSession session, double miles) => TestScenario.Destination_STMD with
    {
        ReferencePosition = session.AircraftState.Position.Project(Heading.FromDegrees(0), miles),
    };

    private AtcDecision Evaluate(AtcSession session, double milesToDestination, IReadOnlyList<TrafficContact>? traffic = null) =>
        _engine.EvaluateStateChange(TestScenario.Context(session, null, traffic: traffic) with
        {
            DestinationAirport = DestinationAt(session, milesToDestination),
        });

    [Fact]
    public void A_fast_flight_inside_35_miles_is_told_to_reduce_to_220_and_the_readback_is_expected()
    {
        AtcDecision decision = Evaluate(Session(), 30);

        Assert.Equal(220, decision.NewAssignedSpeedKnots);
        Assert.Equal("speed-220", decision.NewIssuedInstruction);
        Assert.Contains("reduce speed two two zero knots", decision.FallbackPhraseology, StringComparison.Ordinal);
        PendingReadback readback = decision.RequiredReadback!;
        Assert.Equal(ClearanceType.SpeedInstruction, readback.ClearanceType);
        Assert.False(readback.IsMandatory);
        Assert.Equal("220", readback.ExpectedItems.Single().ExpectedValue);
    }

    [Fact]
    public void Closer_in_it_is_told_to_reduce_to_180_and_each_speed_is_given_once()
    {
        Assert.Equal(180, Evaluate(Session(indicatedAirspeed: 230), 15).NewAssignedSpeedKnots);

        AtcSession done = Session().WithIssuedInstruction("speed-220");
        Assert.Null(Evaluate(done, 30).NewAssignedSpeedKnots);
    }

    [Fact]
    public void No_speed_is_given_to_a_slow_flight_a_vfr_flight_or_one_far_from_its_destination()
    {
        Assert.Null(Evaluate(Session(indicatedAirspeed: 150), 30).NewAssignedSpeedKnots);
        Assert.Null(Evaluate(Session(rules: FlightRules.Vfr), 30).NewAssignedSpeedKnots);
        Assert.Null(Evaluate(Session(), 80).NewAssignedSpeedKnots);
    }

    [Fact]
    public void A_busy_arrival_gets_a_hold_and_is_released_after_three_minutes()
    {
        AtcSession session = Session(indicatedAirspeed: 200);
        AirportData destination = DestinationAt(session, 25);
        TrafficContact[] busy =
        [
            new() { Callsign = "ONE", Position = destination.ReferencePosition, AltitudeFeet = destination.ElevationFeet + 900 },
            new() { Callsign = "TWO", Position = destination.ReferencePosition, AltitudeFeet = destination.ElevationFeet + 1500 },
        ];

        AtcDecision hold = _engine.EvaluateStateChange(TestScenario.Context(session, null, traffic: busy) with { DestinationAirport = destination });

        Assert.NotNull(hold.NewHold);
        Assert.Equal("hold", hold.NewIssuedInstruction);
        Assert.Contains("hold at present position, right-hand pattern", hold.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession holding = session.WithHold(new HoldState { Since = TestScenario.Now - TimeSpan.FromMinutes(4) }).WithIssuedInstruction("hold");
        AtcDecision release = _engine.EvaluateStateChange(TestScenario.Context(holding, null) with { DestinationAirport = destination });

        Assert.True(release.EndsHold);
        Assert.Contains("leave the hold", release.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession waiting = session.WithHold(new HoldState { Since = TestScenario.Now - TimeSpan.FromMinutes(1) });
        Assert.False(_engine.EvaluateStateChange(TestScenario.Context(waiting, null) with { DestinationAirport = destination }).EndsHold);
    }

    [Fact]
    public void The_hold_is_over_the_route_fix_nearest_the_destination()
    {
        AtcSession session = Session(indicatedAirspeed: 200);
        AirportData destination = DestinationAt(session, 25);
        GeoPosition fixPosition = destination.ReferencePosition.Project(Heading.FromDegrees(90), 12);
        session = session with
        {
            FlightPlan = session.FlightPlan! with
            {
                RouteLegs = [new RouteLeg { Identifier = "KILOX", Position = fixPosition }],
            },
        };
        TrafficContact[] busy =
        [
            new() { Callsign = "ONE", Position = destination.ReferencePosition, AltitudeFeet = destination.ElevationFeet + 900 },
            new() { Callsign = "TWO", Position = destination.ReferencePosition, AltitudeFeet = destination.ElevationFeet + 1500 },
        ];

        AtcDecision hold = _engine.EvaluateStateChange(TestScenario.Context(session, null, traffic: busy) with { DestinationAirport = destination });

        Assert.Equal("KILOX", hold.NewHold!.Fix);
        Assert.Equal(ReadbackItemType.HoldFix, hold.RequiredReadback!.ExpectedItems.Single().Type);
    }

    [Fact]
    public void The_centre_gives_a_direct_to_a_fix_ahead_that_shortens_the_route()
    {
        AtcSession session = Session(ControllerPosition.Center, 250, phase: FlightPhase.Enroute);
        AirportData destination = DestinationAt(session, 120);
        GeoPosition ahead = session.AircraftState.Position.Project(Heading.FromDegrees(0), 70);
        session = session with
        {
            FlightPlan = session.FlightPlan! with
            {
                RouteLegs = [new RouteLeg { Identifier = "NOVAR", Position = ahead }],
            },
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null) with { DestinationAirport = destination });

        Assert.Equal(AtcResponseType.DirectToApproval, decision.ResponseType);
        Assert.Contains("cleared direct November", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Equal(ReadbackItemType.RouteFix, decision.RequiredReadback!.ExpectedItems.Single().Type);
        Assert.Equal("direct", decision.NewIssuedInstruction);
    }

    [Fact]
    public void A_correct_readback_of_the_speed_clears_it_and_a_wrong_one_is_corrected()
    {
        AtcSession session = Session() with
        {
            PendingReadback = Evaluate(Session(), 30).RequiredReadback,
        };
        PilotIntent right = TestScenario.Intent(
            PilotIntentType.InstructionReadback,
            "reducing speed 220 knots",
            readbackItems: [new ParsedReadbackItem { Type = ReadbackItemType.Speed, NormalizedValue = "220", SourceText = "220" }]);
        PilotIntent wrong = TestScenario.Intent(
            PilotIntentType.InstructionReadback,
            "reducing speed 240 knots",
            readbackItems: [new ParsedReadbackItem { Type = ReadbackItemType.Speed, NormalizedValue = "240", SourceText = "240" }]);

        Assert.True(_engine.Evaluate(TestScenario.Context(session, right, "reducing speed 220 knots")).ClearsPendingReadback);
        Assert.False(_engine.Evaluate(TestScenario.Context(session, wrong, "reducing speed 240 knots")).ClearsPendingReadback);
    }
}
