using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Atis;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// French radio, both directions: a French transmission is understood and answered in French,
/// through the same deterministic engine as English.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public class FrenchRadioTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly RecordingSessionRepository _repository = new();
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
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

        _context.PreassignedSquawk = SquawkCode.Create("4271");
        _context.UpdateSession(StartingSession());
        _context.SetLanguage(PhraseologyLanguage.French);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    // =====================================================================================
    // Canonical form
    // =====================================================================================

    [Theory]
    [InlineData("Transpondeur trente-deux cinquante-quatre", "squawk 32 54")]
    [InlineData("Transpondeur quarante-cinq vingt et un", "squawk 45 21")]
    [InlineData("Montons niveau cent quatre-vingts", "climbing flight level 180")]
    [InlineData("Montons niveau un huit zéro", "climbing flight level 1 8 0")]
    [InlineData("Descendons altitude deux mille pieds QNH mille treize", "descending altitude 2000 feet qnh 1013")]
    [InlineData("Contactons Tour cent dix-huit décimale sept", "contact tower 118 decimal 7")]
    [InlineData("Contactons Paris 133,375", "contact paris 133.375")]
    [InlineData("Tournons à gauche cap deux cent soixante-dix", "turn left heading 270")]
    [InlineData("Cap zéro quatre-vingt-dix", "heading 0 90")]
    [InlineData("Piste vingt-sept gauche", "runway 27 left")]
    [InlineData("Montée initiale trois mille cinq cents pieds", "climbing 3500 feet")]
    [InlineData("Roulons vers le parking Alpha un", "taxi to stand alpha 1")]
    [InlineData("Demandons un roulage", "request taxi")]
    [InlineData("FL120", "flight level 120")]
    public void French_numbers_and_phrases_become_canonical_values(string french, string expected) =>
        Assert.Equal(expected, FrenchTranscriptCanonicalizer.Canonicalize(french));

    [Theory]
    [InlineData("Storm un deux trois, bonjour, demandons la clairance IFR", PilotIntentType.RequestIFRClearance)]
    [InlineData("Storm 123, prêts à copier", PilotIntentType.RequestIFRClearance)]
    [InlineData("Storm 123, demandons le repoussage, queue vers la gauche", PilotIntentType.RequestPushback)]
    [InlineData("Storm 123, demandons le roulage", PilotIntentType.RequestTaxi)]
    [InlineData("Storm 123, au point d'attente piste dix-huit", PilotIntentType.ReportHoldingShort)]
    [InlineData("Storm 123, au point d'attente piste dix-huit, prêts au départ", PilotIntentType.ReadyForDeparture)]
    [InlineData("Storm 123, piste dégagée", PilotIntentType.ReportRunwayVacated)]
    [InlineData("Storm 123, établis ILS piste 36", PilotIntentType.ReportEstablished)]
    [InlineData("Storm 123, remise de gaz", PilotIntentType.GoAround)]
    [InlineData("Mayday mayday mayday, Storm 123, feu moteur", PilotIntentType.Mayday)]
    [InlineData("Storm 123, répétez", PilotIntentType.SayAgain)]
    [InlineData("Storm 123, quelle est la fréquence de la tour", PilotIntentType.RequestFrequency)]
    [InlineData("Storm 123, demandons direct NOVAR", PilotIntentType.RequestDirect)]
    [InlineData("Storm 123, demandons à monter niveau 240", PilotIntentType.RequestClimb)]
    [InlineData("Paris Départ, bonjour, Storm 123, passant deux mille pieds", PilotIntentType.CheckIn)]
    [InlineData("Paris Départ, bonjour, Storm 123, en montée vers niveau soixante", PilotIntentType.CheckIn)]
    [InlineData("Reçu, merci", PilotIntentType.PilotAcknowledgement)]
    public void French_requests_are_recognised(string french, PilotIntentType expected)
    {
        AtcSession session = _context.CurrentSession;
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));

        PilotIntent intent = DeterministicPilotIntentParser.Parse(normalized, session);

        Assert.Equal(expected, intent.Type);
    }

    // =====================================================================================
    // A French departure, end to end
    // =====================================================================================

    [Fact]
    public async Task A_French_departure_runs_from_clearance_to_takeoff()
    {
        TransmissionProcessingResult clearance = await SendAsync(
            "Storm un deux trois, bonjour, demandons la clairance IFR");
        Assert.Equal(PilotIntentType.RequestIFRClearance, clearance.Intent?.Type);
        Assert.Contains("autorisé", clearance.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains(
            "transpondeur quarante-deux soixante et onze",
            clearance.ControllerSpokenText!,
            StringComparison.Ordinal);

        TransmissionProcessingResult readback = await SendAsync(
            "Autorisés Storm Delta, piste dix-huit, montée initiale sept mille pieds, "
            + "transpondeur quarante-deux soixante et onze, Storm un deux trois");
        Assert.Equal(ReadbackOutcome.Correct, readback.ReadbackAssessment?.Outcome);
        Assert.Contains("contactez Sol", readback.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Equal(ControllerPosition.Ground, _context.CurrentSession.CurrentController);

        TuneToAssignedFrequency();
        TransmissionProcessingResult pushback = await SendAsync(
            "Storm un deux trois, demandons repoussage et mise en route");
        Assert.Equal(AtcResponseType.PushbackApproval, pushback.Decision?.ResponseType);
        Assert.Contains("repoussage approuvé", pushback.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult taxi = await SendAsync("Storm 123, demandons le roulage");
        Assert.Equal(PilotIntentType.RequestTaxi, taxi.Intent?.Type);
        Assert.Contains("roulez point d'attente", taxi.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("maintenez avant piste dix-huit", taxi.ControllerSpokenText!, StringComparison.Ordinal);

        IReadOnlyList<string> route = _context.CurrentSession.TaxiClearance!.TaxiRoute;
        string spokenRoute = string.Join(", ", route.Select(PhoneticAlphabet.SpellOut));
        TransmissionProcessingResult taxiReadback = await SendAsync(
            $"Roulons point d'attente piste dix-huit via {spokenRoute}, "
            + "maintenons avant piste dix-huit, Storm 123");
        Assert.Equal(ReadbackOutcome.Correct, taxiReadback.ReadbackAssessment?.Outcome);

        _context.UpdateAircraftState(_context.CurrentSession.AircraftState with
        {
            Position = TestAerodromes.HoldingPoint18,
            EnginesRunning = true,
            ParkingBrakeSet = false,
        });

        TransmissionProcessingResult holding = await SendAsync(
            "Storm 123, au point d'attente piste dix-huit");
        Assert.Equal(PilotIntentType.ReportHoldingShort, holding.Intent?.Type);
        Assert.Contains("contactez Tour", holding.ControllerSpokenText!, StringComparison.Ordinal);

        TuneToAssignedFrequency();
        TransmissionProcessingResult ready = await SendAsync(
            "Storm 123, au point d'attente piste dix-huit, prêts au départ");
        Assert.Equal(PilotIntentType.ReadyForDeparture, ready.Intent?.Type);
        Assert.Contains("autorisé décollage", ready.ControllerSpokenText!, StringComparison.Ordinal);

        TransmissionProcessingResult takeoffReadback = await SendAsync(
            "Piste dix-huit, autorisés décollage, Storm 123");
        Assert.Equal(ReadbackOutcome.Correct, takeoffReadback.ReadbackAssessment?.Outcome);
        Assert.Equal(FlightPhase.Takeoff, _context.CurrentSession.Phase);

        // The conversation keeps what the pilot said, in French.
        Assert.Contains(
            _context.CurrentSession.Conversation,
            turn => turn.Speaker == ConversationSpeaker.Pilot
                && turn.Text.Contains("autorisés décollage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_French_readback_is_corrected_in_French()
    {
        await SendAsync("Storm un deux trois, demandons la clairance IFR");

        TransmissionProcessingResult result = await SendAsync(
            "Autorisés Storm Delta, piste dix-huit, montée initiale sept mille pieds, "
            + "transpondeur quarante-deux soixante-douze, Storm un deux trois");

        Assert.NotEqual(ReadbackOutcome.Correct, result.ReadbackAssessment?.Outcome);
        Assert.Contains("négatif", result.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains(
            "transpondeur quarante-deux soixante et onze",
            result.ControllerSpokenText!,
            StringComparison.Ordinal);
        Assert.Equal(ControllerPosition.ClearanceDelivery, _context.CurrentSession.CurrentController);
    }

    [Fact]
    public async Task Switching_back_to_English_restores_English_radio()
    {
        _context.SetLanguage(PhraseologyLanguage.English);

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.Equal(PhraseologyLanguage.English, _context.CurrentSession.Language);
        Assert.Contains("four two seven one", result.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_new_session_keeps_the_selected_language()
    {
        AtcSession fresh = _context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(_clock.UtcNow));

        Assert.Equal(PhraseologyLanguage.French, fresh.Language);
    }

    [Fact]
    public void The_suggested_call_is_in_French()
    {
        Guidance.AtcGuidance guidance = Guidance.AtcGuidanceAdvisor.ForSession(_context.CurrentSession);

        Assert.Equal("Storm un deux trois, bonjour, demandons la clairance IFR", guidance.Example);
    }

    // =====================================================================================
    // ATIS
    // =====================================================================================

    [Fact]
    public void The_French_ATIS_reports_the_same_facts_in_French()
    {
        AtcSession session = StartingSession() with
        {
            CurrentController = ControllerPosition.Atis,
            CurrentFrequency = Frequency.FromMegahertz(132.150m),
        };
        var weather = new WeatherSnapshot
        {
            WindDirection = Heading.FromDegrees(270),
            WindSpeedKnots = 12,
            VisibilityMeters = 10_000,
            TemperatureCelsius = -5,
            QnhHectopascals = 1013,
        };
        DateTimeOffset now = new(2026, 7, 28, 14, 30, 0, TimeSpan.Zero);

        AtisBroadcast english = AtisBroadcastBuilder.Build(session, TestAerodromes.Departure, weather, now, 1)!;
        AtisBroadcast french = AtisBroadcastBuilder.Build(
            session with { Language = PhraseologyLanguage.French },
            TestAerodromes.Departure,
            weather,
            now,
            1)!;

        Assert.Equal(english.OperationalSignature, french.OperationalSignature);
        Assert.Equal(english.InformationCode, french.InformationCode);
        Assert.Equal(PhraseologyLanguage.French, french.Language);
        Assert.Contains("enregistrée à quatorze heures trente UTC", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains("Piste en service dix-huit", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains("Vent deux cent soixante-dix degrés, douze nœuds", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains("Visibilité supérieure à dix kilomètres", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains("Température moins cinq", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains("Q N H mille treize", french.SpokenText, StringComparison.Ordinal);
        Assert.Contains(
            $"annoncez avoir reçu l'information {french.InformationCode}",
            french.SpokenText,
            StringComparison.Ordinal);
        Assert.Contains("QNH 1013 hPa", french.DisplayText, StringComparison.Ordinal);
    }

    // =====================================================================================

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    private void TuneToAssignedFrequency()
    {
        AtcSession session = _context.CurrentSession;
        if (session.CurrentFrequency is Frequency assigned)
        {
            _context.UpdateAircraftState(session.AircraftState with
            {
                Radio = session.AircraftState.Radio with { Com1Active = assigned, TransmitOnCom1 = true },
            });
        }
    }

    private static AtcSession StartingSession() => AtcSession
        .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightPlan = new FlightPlan
        {
            FlightPlanId = "test",
            Callsign = Callsign.Create("STORM123"),
            DepartureIcao = "STMR",
            DestinationIcao = "STMD",
            DepartureRunway = RunwayIdentifier.Create("18"),
            ArrivalRunway = RunwayIdentifier.Create("36"),
            StandardInstrumentDeparture = "STORM1A",
            Route = "STORM1A KILOX DCT NOVAR DCT STMD",
            CruiseAltitude = Altitude.FromFlightLevel(120),
            SourceIdentifier = "Test",
        },
        AssignedDepartureRunway = RunwayIdentifier.Create("18"),
        AssignedArrivalRunway = RunwayIdentifier.Create("36"),
        AssignedStand = "A1",
        CurrentController = ControllerPosition.ClearanceDelivery,
        CurrentFrequency = Frequency.FromMegahertz(121.800m),
    };
}
