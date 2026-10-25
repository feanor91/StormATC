using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>Asking a flight information service for a flight following, and tuning it.</summary>
public class FlightFollowingIntentTests
{
    private static AtcSession VfrSession() => AtcSession
        .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightRules = FlightRules.Vfr,
    };

    private static PilotIntent Parse(string french)
    {
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(normalized, VfrSession());
    }

    [Theory]
    [InlineData("Paris Information, Fox-trot Hôtel Alpha Sierra Tango, VFR de Lognes vers Pontoise, demande de suivi")]
    [InlineData("Paris Information, Fox-trot Hôtel Alpha Sierra Tango, demande suivi VFR")]
    [InlineData("Marseille Information, Fox-trot Hôtel Alpha Sierra Tango, suivi VFR")]
    [InlineData("Paris Information, Fox-trot Hôtel Alpha Sierra Tango, demain suivi VFR")]
    [InlineData("Paris Information, Fox-trot Hôtel Alpha Sierra Tango, demandons un suivi VFR")]
    public void A_french_request_for_a_flight_following_is_recognised(string french) =>
        Assert.Equal(PilotIntentType.RequestFlightFollowing, Parse(french).Type);

    [Fact]
    public void An_english_request_for_a_flight_following_is_recognised()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            TranscriptNormalizer.Normalize("Paris Information, Fox-trot Hotel Alpha Sierra Tango, VFR from Lognes to Pontoise, request flight following"),
            VfrSession());

        Assert.Equal(PilotIntentType.RequestFlightFollowing, intent.Type);
    }

    [Fact]
    public void An_arrival_call_is_still_an_arrival_call()
    {
        PilotIntent intent = Parse("Pontoise Tour, Fox-trot Hôtel Alpha Sierra Tango, VFR de Lognes pour un toucher, estimé Echo à zéro cinq");

        Assert.Equal(PilotIntentType.VfrArrivalCall, intent.Type);
    }

    private static AtcSessionContext NewContext() => new(
        new StubAirportDataProvider(),
        new StubTrafficProvider(),
        new StubWeatherProvider(),
        new FixedClock(Fixture.Now),
        NullLogger<AtcSessionContext>.Instance,
        vfrProfileProvider: null);

    private static AircraftState Airborne(Frequency tuned) => TestAerodromes.AtStand(Fixture.Now) with
    {
        IsOnGround = false,
        IndicatedAltitudeFeet = 2500,
        Radio = RadioState.Default with { Com1Active = tuned },
    };

    [Fact]
    public void A_vfr_flight_in_the_air_is_followed_by_the_information_frequency_it_tunes()
    {
        AtcSessionContext context = NewContext();
        context.UpdateSession(VfrSession() with
        {
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = Frequency.FromMegahertz(118.300m),
        });

        context.UpdateAircraftState(Airborne(FlightInformationRegions.ParisSud.Frequency));

        Assert.Equal(ControllerPosition.Information, context.CurrentSession.CurrentController);
        Assert.Equal(FlightInformationRegions.ParisSud.Frequency, context.CurrentSession.CurrentFrequency);
    }

    [Fact]
    public void Tuning_something_else_ends_the_flight_following()
    {
        AtcSessionContext context = NewContext();
        context.UpdateSession(VfrSession() with
        {
            CurrentController = ControllerPosition.Information,
            CurrentFrequency = FlightInformationRegions.ParisSud.Frequency,
            FlightFollowing = new StormATC.Domain.Approach.FlightFollowingState
            {
                ServiceName = "Paris Information",
                Frequency = FlightInformationRegions.ParisSud.Frequency,
                Squawk = SquawkCode.Create("2345"),
                Since = Fixture.Now,
            },
        });

        context.UpdateAircraftState(Airborne(Frequency.FromMegahertz(122.800m)));

        Assert.Null(context.CurrentSession.FlightFollowing);
    }

    [Fact]
    public void An_ifr_flight_is_not_moved_to_the_information_service()
    {
        AtcSessionContext context = NewContext();
        context.UpdateSession(VfrSession() with
        {
            FlightRules = FlightRules.Ifr,
            CurrentController = ControllerPosition.Center,
            CurrentFrequency = Frequency.FromMegahertz(128.500m),
        });

        context.UpdateAircraftState(Airborne(FlightInformationRegions.ParisSud.Frequency));

        Assert.Equal(ControllerPosition.Center, context.CurrentSession.CurrentController);
    }
}
