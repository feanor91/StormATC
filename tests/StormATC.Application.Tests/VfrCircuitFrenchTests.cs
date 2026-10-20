using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// A French VFR circuit through the whole pipeline: the arrival call, the reporting point, each
/// leg of the circuit, a touch and go, and leaving the frequency. The aerodrome is the fictional
/// STMR, given a VFR profile modelled on a real one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public class VfrCircuitFrenchTests : IAsyncLifetime
{
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.300m);

    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly RecordingSessionRepository _repository = new();
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    private sealed class StubVfrProfileProvider : IVfrProfileProvider
    {
        public Task<VfrAerodromeProfile?> GetProfileAsync(string icao, CancellationToken cancellationToken) =>
            Task.FromResult<VfrAerodromeProfile?>(icao.Equals("STMR", StringComparison.OrdinalIgnoreCase)
                ? new VfrAerodromeProfile
                {
                    Icao = "STMR",
                    ReportingPoints = [new VfrReportingPoint("E", "Écho", "Echo")],
                    CircuitHeightFeetAmsl = 1200,
                    CircuitSideBearing = 90,
                }
                : null);
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            new StubWeatherProvider(),
            _clock,
            NullLogger<AtcSessionContext>.Instance,
            new StubVfrProfileProvider());

        _coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            _context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance,
            _repository,
            languageModelRenderer: null,
            new PhraseologyValidator());

        _context.UpdateSession(AtcSession
            .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, Airborne()));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "vfr",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                DepartureRunway = RunwayIdentifier.Create("18"),
                ArrivalRunway = RunwayIdentifier.Create("18"),
                SourceIdentifier = "Test",
            },
            CancellationToken.None);

        _context.SetLanguage(PhraseologyLanguage.French);

        // The pilot flies VFR and has just tuned the tower of the aerodrome it is joining.
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.Vfr });
        _context.UpdateAircraftState(Airborne());
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public async Task A_vfr_arrival_flies_the_circuit_with_a_touch_and_go_and_leaves_the_frequency()
    {
        // Tuning the destination tower makes it the aircraft's controller.
        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);

        TransmissionProcessingResult arrival = await SendAsync(
            "Storm un deux trois, bonjour, PA 28, VFR de Melun pour un toucher, mille deux cents pieds, "
            + "estimé Écho à zéro cinq, information India, demande consignes d'intégration");
        Assert.Equal(PilotIntentType.VfrArrivalCall, arrival.Intent?.Type);
        Assert.EndsWith("reçu, rappelez Écho.", arrival.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.True(_context.CurrentSession.TouchAndGoRequested);

        TransmissionProcessingResult atPoint = await SendAsync("Storm un deux trois, passe Écho");
        Assert.Equal(PilotIntentType.ReportAtPoint, atPoint.Intent?.Type);
        Assert.Contains(
            "entrez vent arrière main gauche piste dix-huit",
            atPoint.ControllerSpokenText!,
            StringComparison.Ordinal);
        Assert.Equal(CircuitLeg.Joining, _context.CurrentSession.CircuitLeg);

        TransmissionProcessingResult joinReadback = await SendAsync(
            "J'entre vent arrière main gauche piste dix-huit, Storm un deux trois");
        Assert.Equal(PilotIntentType.PilotAcknowledgement, joinReadback.Intent?.Type);
        Assert.Equal(CircuitLeg.Joining, _context.CurrentSession.CircuitLeg);

        TransmissionProcessingResult downwind = await SendAsync(
            "Storm un deux trois, vent arrière main gauche piste dix-huit");
        Assert.Equal(PilotIntentType.ReportDownwind, downwind.Intent?.Type);
        Assert.Contains("rappelez base main gauche piste dix-huit", downwind.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult baseLeg = await SendAsync(
            "Storm un deux trois, base main gauche piste dix-huit");
        Assert.Equal(PilotIntentType.ReportBase, baseLeg.Intent?.Type);
        Assert.Contains("rappelez finale piste dix-huit", baseLeg.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult final = await SendAsync("Storm un deux trois, en finale piste dix-huit");
        Assert.Equal(PilotIntentType.ReportFinal, final.Intent?.Type);
        Assert.Equal(AtcResponseType.TouchAndGoClearance, final.Decision?.ResponseType);
        Assert.Contains("autorisé toucher", final.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult readback = await SendAsync(
            "Autorisé toucher piste dix-huit, Storm un deux trois");
        Assert.Equal(ReadbackOutcome.Correct, readback.ReadbackAssessment?.Outcome);

        // Wheels on the runway, then off again: no vacate instruction and no handoff.
        _context.UpdateAircraftState(Landed());
        AtcDecision touchdown = await _coordinator.ProcessStateChangeAsync(CancellationToken.None);
        Assert.Equal(AtcResponseType.None, touchdown.ResponseType);
        Assert.Equal(FlightPhase.Takeoff, _context.CurrentSession.Phase);
        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);

        _context.UpdateAircraftState(Airborne());
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None);
        Assert.Equal(FlightPhase.InitialClimb, _context.CurrentSession.Phase);
        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);

        TransmissionProcessingResult leaving = await SendAsync(
            "Storm un deux trois, sortie de circuit, je quitte la fréquence");
        Assert.Equal(PilotIntentType.LeaveFrequency, leaving.Intent?.Type);
        Assert.Contains("reçu", leaving.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Equal(FlightPhase.Enroute, _context.CurrentSession.Phase);
    }

    [Fact]
    public async Task The_option_asked_in_the_arrival_call_is_cleared_on_final()
    {
        TransmissionProcessingResult arrival = await SendAsync(
            "Storm un deux trois, bonjour, PA 28, VFR de Melun pour une option, mille deux cents pieds, "
            + "estimé Écho à zéro cinq, information India");
        Assert.Equal(PilotIntentType.VfrArrivalCall, arrival.Intent?.Type);
        Assert.Equal(CircuitManoeuvre.Option, _context.CurrentSession.RequestedManoeuvre);

        TransmissionProcessingResult final = await SendAsync("Storm un deux trois, en finale piste dix-huit");
        Assert.Equal(PilotIntentType.ReportFinal, final.Intent?.Type);
        Assert.Equal(AtcResponseType.OptionClearance, final.Decision?.ResponseType);
        Assert.Contains("autorisé option", final.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult readback = await SendAsync("Piste dix-huit, autorisé option, Storm un deux trois");
        Assert.Equal(ReadbackOutcome.Correct, readback.ReadbackAssessment?.Outcome);
    }

    [Fact]
    public async Task A_low_pass_is_requested_in_the_circuit_and_cleared_on_final()
    {
        await SendAsync(
            "Storm un deux trois, bonjour, PA 28, VFR de Melun pour un complet, mille deux cents pieds, "
            + "estimé Écho à zéro cinq, information India");

        TransmissionProcessingResult request = await SendAsync("Storm un deux trois, demandons un passage bas");
        Assert.Equal(PilotIntentType.RequestTouchAndGo, request.Intent?.Type);
        Assert.Equal(CircuitManoeuvre.LowPass, _context.CurrentSession.RequestedManoeuvre);

        TransmissionProcessingResult final = await SendAsync("Storm un deux trois, en finale piste dix-huit");
        Assert.Equal(AtcResponseType.LowPassClearance, final.Decision?.ResponseType);
        Assert.Contains("autorisé passage bas", final.ControllerSpokenText!, StringComparison.Ordinal);
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
        GroundSpeedKnots = 90,
        IndicatedAirspeedKnots = 90,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = false,
        ParkingBrakeSet = false,
        EnginesRunning = true,
        Radio = RadioState.Default with { Com1Active = Tower, TransmitOnCom1 = true },
    };

    private static AircraftState Landed() => Airborne() with
    {
        IndicatedAltitudeFeet = 20,
        PressureAltitudeFeet = 20,
        GroundSpeedKnots = 60,
        IsOnGround = true,
    };
}
