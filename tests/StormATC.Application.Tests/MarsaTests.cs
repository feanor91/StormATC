using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Approach;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>MARSA (a flight of two keeps its own separation) and CAM Tango (a tactical flight), in French.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class MarsaTests : IAsyncLifetime
{
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.300m);

    private readonly FixedClock _clock = new(Fixture.Now);
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            new StubWeatherProvider(),
            _clock,
            NullLogger<AtcSessionContext>.Instance,
            vfrProfileProvider: null);

        _coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            _context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance,
            new RecordingSessionRepository(),
            languageModelRenderer: null,
            new PhraseologyValidator());

        _context.UpdateSession(AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, Airborne()));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "marsa",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                DepartureRunway = RunwayIdentifier.Create("18"),
                ArrivalRunway = RunwayIdentifier.Create("18"),
                SourceIdentifier = "Test",
            },
            CancellationToken.None);

        _context.SetLanguage(PhraseologyLanguage.French);
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.CamV });
        _context.UpdateAircraftState(Airborne());
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public async Task A_flight_of_two_gets_a_marsa_approach_approved_with_its_trail()
    {
        TransmissionProcessingResult request = await SendAsync(
            "Storm un deux trois, patrouille de deux, demande MARSA approche, trail trois milles");

        Assert.Equal(PilotIntentType.RequestMarsa, request.Intent?.Type);
        Assert.Equal(2, request.Intent?.Entities.FlightSize);
        Assert.Equal(3, request.Intent?.Entities.TrailMiles);
        Assert.Contains("MARSA approche piste dix-huit approuvé", request.ControllerSpokenText!, StringComparison.Ordinal);
        MarsaState marsa = _context.CurrentSession.Marsa!;
        Assert.Equal(MarsaKind.Approach, marsa.Kind);
        Assert.Equal(2, marsa.FlightSize);
        Assert.Equal(3, marsa.TrailMiles);
    }

    [Fact]
    public async Task A_marsa_approach_ends_when_the_flight_has_landed()
    {
        await SendAsync("Storm un deux trois, patrouille de deux, demande MARSA approche");
        Assert.NotNull(_context.CurrentSession.Marsa);

        _context.UpdateAircraftState(Airborne() with { IsOnGround = true, IndicatedAltitudeFeet = 20, GroundSpeedKnots = 40 });
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None);

        Assert.Null(_context.CurrentSession.Marsa);
    }

    [Fact]
    public async Task A_marsa_departure_is_asked_for_on_the_ground_with_the_runway_heading()
    {
        _context.UpdateAircraftState(Airborne() with { IsOnGround = true, IndicatedAltitudeFeet = 20, GroundSpeedKnots = 0 });

        TransmissionProcessingResult request = await SendAsync(
            "Storm un deux trois, patrouille de deux, demande MARSA départ piste dix-huit");

        Assert.Equal(PilotIntentType.RequestMarsa, request.Intent?.Type);
        Assert.Contains("MARSA départ piste dix-huit approuvé", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("initial cap", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Equal(MarsaKind.Departure, _context.CurrentSession.Marsa?.Kind);
    }

    [Fact]
    public async Task A_single_aircraft_is_not_given_a_marsa()
    {
        await SendAsync("Storm un deux trois, demande MARSA approche");

        Assert.Null(_context.CurrentSession.Marsa);
    }

    [Fact]
    public async Task A_civil_pilot_is_not_given_a_marsa()
    {
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.Vfr });

        await SendAsync("Storm un deux trois, patrouille de deux, demande MARSA approche");

        Assert.Null(_context.CurrentSession.Marsa);
    }

    [Fact]
    public async Task Cam_tango_is_a_tactical_flight_outside_the_air_traffic_service()
    {
        TransmissionProcessingResult request = await SendAsync("Storm un deux trois, demandons CAM Tango");

        Assert.Equal(PilotIntentType.RequestRegimeChange, request.Intent?.Type);
        Assert.Equal(FlightRules.CamT, _context.CurrentSession.FlightRules);
        Assert.Contains("CAM Tango", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("hors service de la circulation aérienne", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.True(FlightRules.CamT.IsMilitary());
        Assert.False(FlightRules.CamT.IsVisual());
        Assert.False(FlightRules.CamT.IsInstrument());
        Assert.Equal("CAM T", FlightRules.CamT.ToLabel());
        Assert.Equal(FlightRules.CamT, FlightRulesExtensions.FromLabel("CAM T"));
    }

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    private static AircraftState Airborne() => TestAerodromes.AtStand(Fixture.Now) with
    {
        IndicatedAltitudeFeet = 1_200,
        PressureAltitudeFeet = 1_200,
        GroundSpeedKnots = 250,
        IndicatedAirspeedKnots = 250,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = false,
        ParkingBrakeSet = false,
        EnginesRunning = true,
        Radio = RadioState.Default with { Com1Active = Tower, TransmitOnCom1 = true },
    };
}
