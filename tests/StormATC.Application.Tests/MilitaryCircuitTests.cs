using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// A CAM V arrival: first call, initial point, last turn with the gear down and locked, landing clearance
/// (Instruction 1950 DSAE/DIRCAM), in French.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class MilitaryCircuitTests : IAsyncLifetime
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
            new StubWeatherProvider
            {
                Snapshot = new WeatherSnapshot { QnhHectopascals = 1020, WindDirection = Heading.FromDegrees(180), WindSpeedKnots = 8 },
            },
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
                FlightPlanId = "cam",
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
    public async Task A_cam_v_arrival_reports_the_initial_point_then_the_last_turn_and_is_cleared_to_land()
    {
        TransmissionProcessingResult arrival = await SendAsync(
            "Storm un deux trois, vingt nautiques au sud, demande le break");
        Assert.Equal(PilotIntentType.VfrArrivalCall, arrival.Intent?.Type);
        Assert.Contains("rappelez initiale", arrival.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("piste dix-huit", arrival.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Equal(FlightRules.CamV, _context.CurrentSession.FlightRules);

        TransmissionProcessingResult initial = await SendAsync("Storm un deux trois, initiale");
        Assert.Equal(PilotIntentType.ReportInitial, initial.Intent?.Type);
        Assert.Contains("correct, numéro un", initial.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("rappelez dernier virage, train sorti verrouillé", initial.ControllerSpokenText!, StringComparison.Ordinal);

        // Last turn without the gear: the controller asks, and does not clear.
        TransmissionProcessingResult withoutGear = await SendAsync("Storm un deux trois, dernier virage piste dix-huit");
        Assert.Equal(PilotIntentType.ReportFinal, withoutGear.Intent?.Type);
        Assert.Contains("vérifiez train sorti verrouillé", withoutGear.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.NotEqual(AtcResponseType.LandingClearance, withoutGear.Decision?.ResponseType);

        TransmissionProcessingResult gear = await SendAsync("Storm un deux trois, train sorti verrouillé");
        Assert.Equal(PilotIntentType.ReportFinal, gear.Intent?.Type);
        Assert.Equal(AtcResponseType.LandingClearance, gear.Decision?.ResponseType);
        Assert.Contains("autorisé atterrissage", gear.ControllerSpokenText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_turn_with_the_gear_in_the_same_call_is_cleared_at_once()
    {
        await SendAsync("Storm un deux trois, demande le break");
        TransmissionProcessingResult last = await SendAsync(
            "Storm un deux trois, dernier virage piste dix-huit, train sorti verrouillé");

        Assert.Equal(AtcResponseType.LandingClearance, last.Decision?.ResponseType);
    }

    [Fact]
    public async Task A_wrong_qfe_at_the_initial_point_is_corrected()
    {
        await SendAsync("Storm un deux trois, demande le break");
        TransmissionProcessingResult initial = await SendAsync("Storm un deux trois, initiale, QFE neuf cent");

        Assert.Equal(900, initial.Intent?.Entities.QfeHectopascals);
        Assert.Contains("négatif", initial.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("QFE", initial.ControllerSpokenText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_civil_vfr_pilot_saying_final_is_not_asked_for_the_gear()
    {
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.Vfr });

        TransmissionProcessingResult final = await SendAsync("Storm un deux trois, en finale piste dix-huit");

        Assert.DoesNotContain("train", final.ControllerSpokenText ?? string.Empty, StringComparison.Ordinal);
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
