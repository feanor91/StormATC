using StormATC.Domain.Airports;
using StormATC.Domain.Approach;
using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>The VFR flight following: the flight information services of France and what the engine does with them.</summary>
public class FlightFollowingTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    private static readonly GeoPosition Lognes = GeoPosition.Create(48.82, 2.63);
    private static readonly GeoPosition Pontoise = GeoPosition.Create(49.10, 2.04);
    private static readonly GeoPosition Brest = GeoPosition.Create(48.44, -4.42);
    private static readonly GeoPosition Bordeaux = GeoPosition.Create(44.83, -0.72);
    private static readonly GeoPosition Deauville = GeoPosition.Create(49.30, 0.15);
    private static readonly GeoPosition Marseille = GeoPosition.Create(43.44, 5.21);
    private static readonly GeoPosition Grenoble = GeoPosition.Create(45.36, 5.33);
    private static readonly GeoPosition Ajaccio = GeoPosition.Create(41.92, 8.80);

    [Theory]
    [InlineData(48.82, 2.63, "Paris Information", "Sud", 126.100)]
    [InlineData(49.10, 2.04, "Paris Information", "Nord", 125.700)]
    [InlineData(49.30, 0.15, "Paris Information", "Ouest", 129.625)]
    [InlineData(48.60, 7.80, "Paris Information", "Nord", 125.700)]
    [InlineData(43.44, 5.21, "Marseille Information", "Sud", 120.550)]
    [InlineData(45.36, 5.33, "Marseille Information", "Nord", 124.500)]
    [InlineData(41.92, 8.80, "Marseille Information", "Sud", 120.550)]
    public void A_position_is_served_by_the_information_service_of_its_region(
        double latitude, double longitude, string service, string sector, double megahertz)
    {
        FlightInformationSector? found = FlightInformationRegions.Find(GeoPosition.Create(latitude, longitude));

        Assert.NotNull(found);
        Assert.Equal(service, found.ServiceName);
        Assert.Equal(sector, found.Sector);
        Assert.Equal(Frequency.FromMegahertz((decimal)megahertz), found.Frequency);
    }

    [Theory]
    [InlineData(48.44, -4.42)]
    [InlineData(44.83, -0.72)]
    [InlineData(51.9, 2.0)]
    [InlineData(40.0, 3.0)]
    public void No_information_service_is_published_in_the_west_the_south_west_or_abroad(double latitude, double longitude) =>
        Assert.Null(FlightInformationRegions.Find(GeoPosition.Create(latitude, longitude)));

    [Fact]
    public void A_published_information_frequency_is_recognised_and_another_is_not()
    {
        Assert.Equal("Sud", FlightInformationRegions.FindByFrequency(Frequency.FromMegahertz(126.100m))!.Sector);
        Assert.Null(FlightInformationRegions.FindByFrequency(TestScenario.Tower));
    }

    private static AtcSession FollowedSession(
        GeoPosition position,
        FlightInformationSector serving,
        PhraseologyLanguage language = PhraseologyLanguage.English,
        bool onGround = false) => TestScenario.NewSession(
            (onGround ? TestScenario.AtHoldingPoint() : TestScenario.Airborne(2500)) with { Position = position })
        with
        {
            FlightRules = FlightRules.Vfr,
            CurrentController = ControllerPosition.Information,
            CurrentFrequency = serving.Frequency,
            Phase = FlightPhase.Enroute,
            Language = language,
        };

    private static PilotIntent Request() => TestScenario.Intent(
        PilotIntentType.RequestFlightFollowing,
        "request flight following",
        entities: new IntentEntities { DeclaresVfr = true });

    [Fact]
    public void A_flight_following_is_given_by_the_service_of_the_region_with_a_transponder_code()
    {
        AtcSession session = FollowedSession(Lognes, FlightInformationRegions.ParisSud);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, Request()));

        Assert.True(decision.Accepted);
        FlightFollowingState following = decision.NewFlightFollowing!;
        Assert.Equal("Paris Information", following.ServiceName);
        Assert.Equal(FlightInformationRegions.ParisSud.Frequency, following.Frequency);
        Assert.Contains("Paris Information", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("squawk", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flight_following_is_given_in_french_to_a_french_session()
    {
        AtcSession session = FollowedSession(Lognes, FlightInformationRegions.ParisSud, PhraseologyLanguage.French);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, Request()));

        Assert.Contains("suivi accordé", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Contains("affichez", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_for_a_flight_following_to_another_controller_is_sent_to_the_information_service()
    {
        AtcSession session = FollowedSession(Lognes, FlightInformationRegions.ParisSud) with
        {
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = TestScenario.Tower,
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, Request()));

        Assert.False(decision.Accepted);
        Assert.Contains("Paris Information", decision.FallbackPhraseology, StringComparison.Ordinal);
        Assert.Null(decision.NewFlightFollowing);
    }

    [Fact]
    public void Where_no_service_is_published_the_request_is_refused()
    {
        AtcSession session = FollowedSession(Brest, FlightInformationRegions.ParisOuest);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, Request()));

        Assert.False(decision.Accepted);
        Assert.Contains("no flight information service", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_following_is_not_given_on_the_ground_nor_to_an_ifr_flight()
    {
        AtcSession ground = FollowedSession(Lognes, FlightInformationRegions.ParisSud, onGround: true);
        Assert.False(_engine.Evaluate(TestScenario.Context(ground, Request())).Accepted);

        AtcSession ifr = FollowedSession(Lognes, FlightInformationRegions.ParisSud) with { FlightRules = FlightRules.Ifr };
        PilotIntent notVfr = TestScenario.Intent(PilotIntentType.RequestFlightFollowing, "request flight following");
        Assert.False(_engine.Evaluate(TestScenario.Context(ifr, notVfr)).Accepted);
    }

    private static AtcSession Following(AtcSession session, FlightInformationSector serving, TimeSpan ago) => session.WithFlightFollowing(
        new FlightFollowingState
        {
            ServiceName = serving.ServiceName,
            Frequency = serving.Frequency,
            Squawk = SquawkCode.Create("2345"),
            Since = TestScenario.Now - ago,
        });

    [Fact]
    public void Crossing_into_another_sector_hands_the_flight_to_it_and_it_stays_followed()
    {
        AtcSession session = Following(
            FollowedSession(Lognes, FlightInformationRegions.ParisNord),
            FlightInformationRegions.ParisNord,
            TimeSpan.FromMinutes(20));

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null));

        Assert.Equal(AtcResponseType.Handoff, decision.ResponseType);
        Assert.Equal(FlightInformationRegions.ParisSud.Frequency, decision.Handoff!.ToFrequency);
        Assert.Equal(ControllerPosition.Information, decision.Handoff.ToController);
        Assert.Equal(FlightInformationRegions.ParisSud.Frequency, decision.NewFlightFollowing!.Frequency);
        Assert.False(decision.EndsFlightFollowing);
        Assert.Contains("contact Paris Information", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_just_handed_over_is_not_handed_again_at_once()
    {
        AtcSession session = Following(
            FollowedSession(Lognes, FlightInformationRegions.ParisNord),
            FlightInformationRegions.ParisNord,
            TimeSpan.FromSeconds(30));

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null));

        Assert.Null(decision.Handoff);
        Assert.Null(decision.NewFlightFollowing);
    }

    [Fact]
    public void Leaving_the_covered_area_ends_the_following_without_handing_the_flight_anywhere()
    {
        AtcSession session = Following(
            FollowedSession(Bordeaux, FlightInformationRegions.ParisSud),
            FlightInformationRegions.ParisSud,
            TimeSpan.FromMinutes(20));

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null));

        Assert.True(decision.EndsFlightFollowing);
        Assert.Null(decision.Handoff);
        Assert.Contains("flight following terminated", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void Reaching_the_destination_ends_the_following_and_hands_the_flight_to_its_approach()
    {
        AirportData destination = TestScenario.Destination_STMD with
        {
            ReferencePosition = Pontoise.Project(Heading.FromDegrees(90), 5),
            Frequencies = new AirportFrequencies
            {
                Tower = TestScenario.Tower,
                Approach = TestScenario.Approach,
            },
        };
        AtcSession session = Following(
            FollowedSession(Pontoise, FlightInformationRegions.ParisNord),
            FlightInformationRegions.ParisNord,
            TimeSpan.FromMinutes(1));

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, null) with { DestinationAirport = destination });

        Assert.True(decision.EndsFlightFollowing);
        Assert.Equal(ControllerPosition.Approach, decision.Handoff!.ToController);
        Assert.Equal(TestScenario.Approach, decision.Handoff.ToFrequency);
        Assert.Contains("flight following terminated", decision.FallbackPhraseology, StringComparison.Ordinal);
    }

    [Fact]
    public void Unused_positions_keep_the_regions_test_data_honest()
    {
        // Every position the tests rely on is covered by exactly the service they name.
        Assert.NotNull(FlightInformationRegions.Find(Deauville));
        Assert.NotNull(FlightInformationRegions.Find(Marseille));
        Assert.NotNull(FlightInformationRegions.Find(Grenoble));
        Assert.NotNull(FlightInformationRegions.Find(Ajaccio));
    }
}
