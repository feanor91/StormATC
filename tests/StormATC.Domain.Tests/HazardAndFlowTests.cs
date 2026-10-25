using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>Flow control, military areas and weather, the parallel offset, the CPDLC failure and the radar sequence.</summary>
public class HazardAndFlowTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    private static AtcSession Session(
        ControllerPosition controller = ControllerPosition.Center,
        double indicatedAirspeed = 200,
        FlightPhase phase = FlightPhase.Enroute) => TestScenario.NewSession(TestScenario.Airborne(20_000) with { IndicatedAirspeedKnots = indicatedAirspeed })
        with
    {
        FlightRules = FlightRules.Ifr,
        CurrentController = controller,
        CurrentFrequency = TestScenario.Center,
        Phase = phase,
    };

    private static HazardArea Area(GeoPosition center, double radius = 5, string name = "R45", HazardKind kind = HazardKind.MilitaryArea) => new()
    {
        Name = name,
        Kind = kind,
        Center = center,
        RadiusNauticalMiles = radius,
    };

    private static AtcSession WithRoute(AtcSession session, string fix, GeoPosition position) => session with
    {
        FlightPlan = session.FlightPlan! with { RouteLegs = [new RouteLeg { Identifier = fix, Position = position }] },
    };

    private AtcDecision Ask(AtcSession session, PilotIntentType type, string transcript, IntentEntities? entities = null, AtcEvaluationContext? basis = null) =>
        _engine.Evaluate((basis ?? TestScenario.Context(session, null)) with
        {
            Session = session,
            Intent = TestScenario.Intent(type, transcript, entities: entities),
            Transcript = transcript,
        });

    [Fact]
    public void A_path_that_crosses_an_area_is_measured_and_a_path_that_misses_it_is_not()
    {
        GeoPosition start = TestScenario.Runway18Midpoint;
        HazardArea area = Area(start.Project(Heading.FromDegrees(180), 20));

        Assert.True(area.CrossesPath(start, start.Project(Heading.FromDegrees(180), 40), 3));
        Assert.False(area.CrossesPath(start, start.Project(Heading.FromDegrees(90), 40), 3));
        Assert.False(area.CrossesPath(start, start.Project(Heading.FromDegrees(180), 10), 3));
        Assert.False((area with { FloorFeet = 25_000 }).CoversAltitude(20_000));
    }

    [Fact]
    public void A_direct_is_refused_when_the_arrival_is_saturated()
    {
        AtcSession session = WithRoute(Session(), "NOVAR", TestScenario.Runway18Midpoint.Project(Heading.FromDegrees(180), 50));
        GeoPosition airport = TestScenario.Destination_STMD.ReferencePosition;
        TrafficContact[] busy =
        [
            new() { Callsign = "ONE", Position = airport, AltitudeFeet = 900 },
            new() { Callsign = "TWO", Position = airport, AltitudeFeet = 1_200 },
            new() { Callsign = "THREE", Position = airport, AltitudeFeet = 1_500 },
        ];

        AtcDecision refused = Ask(
            session,
            PilotIntentType.RequestDirect,
            "request direct novar",
            new IntentEntities { Waypoint = "NOVAR" },
            TestScenario.Context(session, null, traffic: busy));

        Assert.False(refused.Accepted);
        Assert.Contains("flow restriction", refused.FallbackPhraseology, StringComparison.Ordinal);

        AtcDecision allowed = Ask(session, PilotIntentType.RequestDirect, "request direct novar", new IntentEntities { Waypoint = "NOVAR" });
        Assert.True(allowed.Accepted);
    }

    [Fact]
    public void A_direct_and_a_new_route_through_an_active_area_are_refused()
    {
        GeoPosition start = TestScenario.Runway18Midpoint;
        AtcSession session = WithRoute(Session(), "NOVAR", start.Project(Heading.FromDegrees(180), 50));
        HazardArea area = Area(start.Project(Heading.FromDegrees(180), 25));
        AtcEvaluationContext context = TestScenario.Context(session, null) with { Hazards = [area] };

        AtcDecision direct = Ask(session, PilotIntentType.RequestDirect, "request direct novar", new IntentEntities { Waypoint = "NOVAR" }, context);
        AtcDecision route = Ask(session, PilotIntentType.RequestRouteChange, "request new route via novar", basis: context);

        Assert.False(direct.Accepted);
        Assert.Contains("military area R45", direct.FallbackPhraseology, StringComparison.Ordinal);
        Assert.False(route.Accepted);
        Assert.Contains("military area R45", route.FallbackPhraseology, StringComparison.Ordinal);

        AtcEvaluationContext inactive = context with { Hazards = [area with { IsActive = false }] };
        Assert.True(Ask(session, PilotIntentType.RequestDirect, "request direct novar", new IntentEntities { Waypoint = "NOVAR" }, inactive).Accepted);
    }

    [Fact]
    public void The_controller_turns_a_flight_away_from_an_area_on_its_path_once_and_releases_it_after()
    {
        AtcSession session = Session();
        HazardArea area = Area(session.AircraftState.Position.Project(Heading.FromDegrees(180), 20));
        AtcEvaluationContext context = TestScenario.Context(session, null) with { Hazards = [area] };

        AtcDecision turn = _engine.EvaluateStateChange(context);

        Assert.Equal(AtcResponseType.HeadingInstruction, turn.ResponseType);
        Assert.Equal("hazard-R45", turn.NewIssuedInstruction);
        Assert.Contains("to avoid military area R45, turn right heading", turn.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Equal(ReadbackItemType.Heading, turn.RequiredReadback!.ExpectedItems.Single().Type);
        Assert.False(turn.RequiredReadback.IsMandatory);

        AtcSession turned = session.WithIssuedInstruction("hazard-R45");
        Assert.NotEqual("hazard-R45", _engine.EvaluateStateChange(context with { Session = turned }).NewIssuedInstruction);

        HazardArea behind = Area(session.AircraftState.Position.Project(Heading.FromDegrees(0), 12));
        AtcDecision released = _engine.EvaluateStateChange(TestScenario.Context(turned, null) with { Hazards = [behind] });
        Assert.Equal("hazard-clear-R45", released.NewIssuedInstruction);
        Assert.Contains("resume own navigation", released.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_area_at_another_level_or_off_the_path_changes_nothing()
    {
        AtcSession session = Session();
        GeoPosition position = session.AircraftState.Position;
        HazardArea high = Area(position.Project(Heading.FromDegrees(180), 20)) with { FloorFeet = 30_000 };
        HazardArea aside = Area(position.Project(Heading.FromDegrees(90), 20));

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null) with { Hazards = [high, aside] });

        Assert.True(decision.NewIssuedInstruction is null || !decision.NewIssuedInstruction.StartsWith("hazard", StringComparison.Ordinal));
    }

    [Fact]
    public void A_weather_area_is_named_as_bad_weather()
    {
        AtcSession session = Session();
        HazardArea storm = Area(session.AircraftState.Position.Project(Heading.FromDegrees(180), 20), name: "CB", kind: HazardKind.Weather);

        AtcDecision turn = _engine.EvaluateStateChange(TestScenario.Context(session, null) with { Hazards = [storm] });

        Assert.Contains("weather area CB", turn.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_is_approved_with_its_distance_and_side_and_ends_when_back_on_route()
    {
        AtcSession session = Session();

        AtcDecision approved = Ask(session, PilotIntentType.RequestOffset, "request offset 3 miles left");

        Assert.True(approved.Accepted);
        Assert.Equal(3, approved.NewOffsetMiles);
        Assert.Contains("offset three miles left of route", approved.FallbackPhraseology, StringComparison.Ordinal);

        AtcSession offset = session.WithOffset(3);
        Assert.Equal(3, offset.OffsetMiles);
        AtcDecision back = Ask(offset, PilotIntentType.ReportBackOnRoute, "back on route");
        Assert.Equal(0, back.NewOffsetMiles);
        Assert.Null(offset.WithOffset(0).OffsetMiles);

        AtcDecision nothing = Ask(session, PilotIntentType.ReportBackOnRoute, "back on route");
        Assert.Null(nothing.NewOffsetMiles);
    }

    [Fact]
    public void An_offset_without_a_distance_is_two_miles_to_the_right_and_one_too_far_is_refused()
    {
        AtcSession session = Session();

        AtcDecision plain = Ask(session, PilotIntentType.RequestOffset, "request offset");
        Assert.Equal(2, plain.NewOffsetMiles);
        Assert.Contains("right of route", plain.FallbackPhraseology, StringComparison.Ordinal);

        Assert.False(Ask(session, PilotIntentType.RequestOffset, "request offset 30 miles").Accepted);
        Assert.False(Ask(Session(phase: FlightPhase.Approach), PilotIntentType.RequestOffset, "request offset 2 miles").Accepted);
    }

    [Fact]
    public void A_cpdlc_failure_sends_the_flight_back_to_voice()
    {
        AtcDecision decision = Ask(Session(), PilotIntentType.ReportCpdlcFailure, "cpdlc failure");

        Assert.True(decision.Accepted);
        Assert.Contains("continue on voice", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void An_arrival_behind_another_is_given_its_number_in_the_radar_sequence_once()
    {
        AtcSession session = Session(ControllerPosition.Approach, phase: FlightPhase.Descent) with { CurrentFrequency = TestScenario.Approach };
        AirportData destination = TestScenario.Destination_STMD with
        {
            ReferencePosition = session.AircraftState.Position.Project(Heading.FromDegrees(0), 30),
        };
        TrafficContact[] ahead =
        [
            new() { Callsign = "ONE", Position = destination.ReferencePosition.Project(Heading.FromDegrees(180), 10), AltitudeFeet = 5_000 },
        ];
        AtcEvaluationContext context = TestScenario.Context(session, null, traffic: ahead) with { DestinationAirport = destination };

        AtcDecision first = _engine.EvaluateStateChange(context);

        Assert.Equal("sequence", first.NewIssuedInstruction);
        Assert.Contains("number two for the approach", first.FallbackPhraseology, StringComparison.Ordinal);
        Assert.NotEqual("sequence", _engine.EvaluateStateChange(context with { Session = session.WithIssuedInstruction("sequence") }).NewIssuedInstruction);
    }

    [Fact]
    public void A_direct_the_controller_would_give_is_not_given_through_an_area()
    {
        AtcSession session = Session();
        AirportData destination = TestScenario.Destination_STMD with
        {
            ReferencePosition = session.AircraftState.Position.Project(Heading.FromDegrees(0), 120),
        };
        GeoPosition fix = session.AircraftState.Position.Project(Heading.FromDegrees(0), 70);
        session = WithRoute(session, "NOVAR", fix);
        AtcEvaluationContext open = TestScenario.Context(session, null) with { DestinationAirport = destination };

        Assert.Equal("direct", _engine.EvaluateStateChange(open).NewIssuedInstruction);

        HazardArea between = Area(session.AircraftState.Position.Project(Heading.FromDegrees(0), 40), radius: 6);
        AtcDecision blocked = _engine.EvaluateStateChange(open with { Hazards = [between] });
        Assert.NotEqual("direct", blocked.NewIssuedInstruction);
    }
}
