using Microsoft.Extensions.Logging.Abstractions;
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
/// A French VFR departure, from the first call to the takeoff clearance, with the transcripts the
/// speech recogniser really produced.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public class VfrDepartureFrenchTests : IAsyncLifetime
{
    private static readonly Frequency Ground = Frequency.FromMegahertz(121.700m);
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.300m);

    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly RecordingSessionRepository _repository = new();
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
            NullLogger<AtcSessionContext>.Instance);

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

        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, Tuned(Ground)));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "vfr",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                SourceIdentifier = "Test",
            },
            CancellationToken.None);
        _context.SetLanguage(PhraseologyLanguage.French);
        _context.Mutate(session => session.WithFlightRules(FlightRules.Vfr));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Theory]
    [InlineData("Storm un deux trois, au point d'attente, pistes assignées, prés au départ")]
    [InlineData("Storm un deux trois, point d'attente, piste dix-huit, près au départ")]
    [InlineData("Storm un deux trois, au point d'attente piste dix-huit, prêts au départ")]
    [InlineData("Storm un deux trois, prêt à partir")]
    [InlineData("Storm un deux trois, prêts à décoller")]
    public void However_it_is_heard_ready_for_departure_is_ready_for_departure(string french)
    {
        string canonical = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));

        PilotIntent intent = DeterministicPilotIntentParser.Parse(canonical, _context.CurrentSession);

        Assert.Equal(PilotIntentType.ReadyForDeparture, intent.Type);
    }

    [Fact]
    public async Task A_local_vfr_departure_reaches_the_takeoff_clearance_on_the_runway_of_the_taxi_clearance()
    {
        // Straight to Ground: a VFR flight has no ATIS-first order and no clearance to ask for.
        _context.UpdateAircraftState(Tuned(Ground));
        Assert.Equal(ControllerPosition.Ground, _context.CurrentSession.CurrentController);

        TransmissionProcessingResult taxi = await SendAsync("Storm un deux trois, vol local, demandons le roulage");
        Assert.Equal(AtcResponseType.TaxiClearance, taxi.Decision?.ResponseType);
        Assert.Equal(FlightRules.Vfr, _context.CurrentSession.FlightRules);

        // The runway named in the taxi clearance is the runway of the departure.
        RunwayIdentifier runway = taxi.Decision!.IssuedClearance!.Runway!.Value;
        Assert.Equal(runway, _context.CurrentSession.AssignedDepartureRunway);

        string spoken = runway.Number.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        TransmissionProcessingResult readback = await SendAsync(
            taxi.ControllerSpokenText!.Replace("roulez point d'attente", "roulons point d'attente", StringComparison.Ordinal));
        Assert.Equal(ReadbackOutcome.Correct, readback.ReadbackAssessment?.Outcome);

        // At the holding point, Ground hands the aircraft to Tower.
        TransmissionProcessingResult holding = await SendAsync(
            $"Storm un deux trois, au point d'attente piste {spoken}");
        Assert.Equal(PilotIntentType.ReportHoldingShort, holding.Intent?.Type);
        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);

        _context.UpdateAircraftState(Tuned(Tower));

        // "Prés au départ", as the speech recogniser wrote it, is still ready for departure.
        TransmissionProcessingResult ready = await SendAsync(
            "Storm un deux trois, au point d'attente, pistes assignées, prés au départ");

        Assert.Equal(PilotIntentType.ReadyForDeparture, ready.Intent?.Type);
        Assert.Equal(AtcResponseType.TakeoffClearance, ready.Decision?.ResponseType);
        Assert.Contains("autorisé décollage", ready.ControllerSpokenText!, StringComparison.Ordinal);
    }

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    private static AircraftState Tuned(Frequency frequency) => TestAerodromes.AtStand(Fixture.Now) with
    {
        ParkingBrakeSet = false,
        EnginesRunning = true,
        Radio = RadioState.Default with { Com1Active = frequency, TransmitOnCom1 = true },
    };
}
