using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Options;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Data;
using StormATC.Infrastructure.Mocks;
using StormATC.Infrastructure.Persistence;
using StormATC.Infrastructure.Simulator;
using StormATC.Infrastructure.Storage;

namespace StormATC.Infrastructure.Tests;

/// <summary>
/// The whole mock IFR flight, driven through the real pipeline from end to end.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that says the prototype works. It uses the bundled fictional aerodrome data,
/// the real deterministic ATC engine, the real intent parser, the real readback evaluator, the
/// real phraseology renderer, the real mock simulator, and a real SQLite database in a temporary
/// directory. Nothing about the ATC pipeline is stubbed.
/// </para>
/// <para>
/// What is deliberately absent is any cloud service: no OpenRouter, no Deepgram. That is the
/// point — it proves the application is complete and correct with no API keys at all, and it
/// exercises exactly the code path a user hits when a provider is down.
/// </para>
/// <para>
/// Transmissions are typed rather than spoken. That bypasses speech-to-text and nothing else:
/// normalisation, parsing, the engine, persistence, and rendering are all the production ones.
/// </para>
/// </remarks>
public sealed class EndToEndMockSessionTests : IAsyncLifetime, IDisposable
{
    private readonly TemporaryStorage _storage = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;
    private MockSimulatorConnector _simulator = null!;
    private MockTrafficProvider _traffic = null!;
    private SqliteSessionRepository _repository = null!;

    /// <summary>Every controller position the session passed through, in order.</summary>
    private readonly List<ControllerPosition> _controllerHistory = [];

    /// <summary>Every flight phase the session passed through, in order.</summary>
    private readonly List<FlightPhase> _phaseHistory = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var paths = _storage.Paths;

        _repository = new SqliteSessionRepository(
            paths, Options.Create(new PersistenceOptions()), NullLogger<SqliteSessionRepository>.Instance);

        await _repository.InitializeAsync(CancellationToken.None);

        _traffic = new MockTrafficProvider();
        _simulator = new MockSimulatorConnector(NullLogger<MockSimulatorConnector>.Instance, _clock);

        _context = new AtcSessionContext(
            new JsonAirportDataProvider(paths, NullLogger<JsonAirportDataProvider>.Instance),
            _traffic,
            new MockWeatherProvider(),
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

            // No language model at all. Deterministic phraseology is the whole output.
            languageModelRenderer: null,
            new PhraseologyValidator());

        _context.SessionChanged += RecordTransition;

        // Load the bundled sample flight, exactly as the application does at startup.
        var flightPlanProvider = new MockFlightPlanProvider(
            paths, NullLogger<MockFlightPlanProvider>.Instance);

        FlightPlanImportResult import = await flightPlanProvider.ImportLatestAsync(
            new FlightPlanImportRequest(), CancellationToken.None);

        Assert.True(import.Success, import.ErrorMessage);

        _context.StartNewSession(SessionMode.Mock, _simulator.GetCurrentState());
        _context.PreassignedSquawk = flightPlanProvider.PreassignedSquawk;
        await _context.LoadFlightPlanAsync(import.FlightPlan!, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _context.SessionChanged -= RecordTransition;
        await _coordinator.DisposeAsync();
        await _simulator.DisposeAsync();
        Dispose();
    }

    /// <summary>Releases the database and the temporary directory.</summary>
    public void Dispose()
    {
        _repository?.Dispose();
        _storage.Dispose();
    }

    private void RecordTransition(object? sender, AtcSession session)
    {
        if (_phaseHistory.Count == 0 || _phaseHistory[^1] != session.Phase)
        {
            _phaseHistory.Add(session.Phase);
        }

        if (_controllerHistory.Count == 0 || _controllerHistory[^1] != session.CurrentController)
        {
            _controllerHistory.Add(session.CurrentController);
        }
    }

    // =====================================================================================

    [Fact]
    public async Task A_complete_mock_IFR_flight_runs_from_clearance_request_to_parked()
    {
        AtcSession session = _context.CurrentSession;

        Assert.Equal("STORM123", session.Callsign?.Normalized);
        Assert.Equal(ControllerPosition.ClearanceDelivery, session.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(121.800m), session.CurrentFrequency);

        // --- 1. IFR clearance ------------------------------------------------------------
        TransmissionProcessingResult clearance = await SayAsync("storm one two three request ifr clearance");

        Clearance issued = Assert.IsType<Clearance>(clearance.Decision?.IssuedClearance);
        Assert.Equal("STMD", issued.ClearedToDestination);
        Assert.Equal(RunwayIdentifier.Create("18"), issued.Runway);
        Assert.Equal(SquawkCode.Create("4271"), issued.Squawk);
        Assert.Equal(7_000, issued.InitialAltitude?.Feet);
        Assert.NotNull(_context.CurrentSession.PendingReadback);

        AssertNoInventedValues(clearance);

        // --- 2. Readback, checked item by item -------------------------------------------
        TransmissionProcessingResult readback = await SayAsync(
            "cleared to storm delta runway one eight climb seven thousand "
            + "squawk four two seven one storm one two three");

        Assert.Equal(ReadbackOutcome.Correct, readback.ReadbackAssessment?.Outcome);
        Assert.Null(_context.CurrentSession.PendingReadback);

        // Delivery hands the aircraft to Ground on the published frequency.
        Assert.Equal(ControllerPosition.Ground, _context.CurrentSession.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(121.700m), _context.CurrentSession.CurrentFrequency);

        // --- 3. Pushback -------------------------------------------------------------------
        TransmissionProcessingResult pushback = await SayAsync("storm one two three request pushback");

        Assert.True(pushback.Decision?.Accepted);
        Assert.Equal(FlightPhase.Pushback, _context.CurrentSession.Phase);
        await AdvanceSimulatorToAsync(MockFlightStage.PushedBack);

        // --- 4. Taxi ------------------------------------------------------------------------
        TransmissionProcessingResult taxi = await SayAsync("storm one two three request taxi");

        Clearance taxiClearance = Assert.IsType<Clearance>(taxi.Decision?.IssuedClearance);
        Assert.NotEmpty(taxiClearance.TaxiRoute);
        Assert.Equal(RunwayIdentifier.Create("18"), taxiClearance.HoldShortOfRunway);
        AssertNoInventedValues(taxi);

        // The hold-short instruction must be read back before the aircraft may move on.
        Assert.Contains(
            _context.CurrentSession.PendingReadback!.ExpectedItems,
            item => item.Type == ReadbackItemType.HoldShortRunway);

        string spokenRoute = string.Join(" ", taxiClearance.TaxiRoute.Select(SpokenTaxiway));

        await SayAsync($"taxi via {spokenRoute} hold short runway one eight storm one two three");
        Assert.Null(_context.CurrentSession.PendingReadback);

        await AdvanceSimulatorToAsync(MockFlightStage.Taxiing);
        await AdvanceSimulatorToAsync(MockFlightStage.HoldingShort);

        // --- 5. Holding short, handed to Tower ------------------------------------------------
        await SayAsync("storm one two three holding short runway one eight");

        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(118.300m), _context.CurrentSession.CurrentFrequency);

        // --- 6. Traffic on the runway blocks the takeoff clearance -----------------------------
        _traffic.OccupyRunway(
            RunwayIdentifier.Create("18"), GeoPosition.Create(52.30050, 4.70000));

        TransmissionProcessingResult blocked = await SayAsync("storm one two three ready for departure");

        Assert.False(blocked.Decision?.Accepted);
        Assert.Null(blocked.Decision?.IssuedClearance);
        Assert.DoesNotContain(
            "cleared for takeoff", blocked.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);

        _traffic.Clear();

        // --- 7. Takeoff clearance ---------------------------------------------------------------
        TransmissionProcessingResult takeoff = await SayAsync("storm one two three ready for departure");

        Clearance takeoffClearance = Assert.IsType<Clearance>(takeoff.Decision?.IssuedClearance);
        Assert.Equal(ClearanceType.Takeoff, takeoffClearance.Type);
        Assert.Equal(RunwayIdentifier.Create("18"), takeoffClearance.Runway);
        AssertNoInventedValues(takeoff);

        await SayAsync("runway one eight cleared for takeoff storm one two three");
        Assert.Null(_context.CurrentSession.PendingReadback);

        // --- 8. Airborne, handed to Departure ----------------------------------------------------
        await AdvanceSimulatorToAsync(MockFlightStage.TakeoffRoll);
        await AdvanceSimulatorToAsync(MockFlightStage.Climbing);

        Assert.Equal(FlightPhase.InitialClimb, _context.CurrentSession.Phase);

        // The engine hands off once the aircraft is established in the climb.
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None);

        Assert.Equal(ControllerPosition.Departure, _context.CurrentSession.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(119.100m), _context.CurrentSession.CurrentFrequency);

        // --- 9. Check in, and read back the climb that comes with it -----------------------------
        TransmissionProcessingResult checkIn = await SayAsync(
            "departure storm one two three checking in climbing through three thousand");

        Assert.True(checkIn.Decision?.Accepted, checkIn.Decision?.RejectionReason ?? "no decision");
        AssertNoInventedValues(checkIn);

        // Departure's check-in response clears the aircraft to its filed cruise level, which is
        // a clearance and therefore owes a readback.
        Clearance climbClearance = Assert.IsType<Clearance>(checkIn.Decision?.IssuedClearance);
        Assert.Equal(ClearanceType.Climb, climbClearance.Type);
        Assert.Equal(12_000, climbClearance.InitialAltitude?.Feet);
        Assert.NotNull(_context.CurrentSession.PendingReadback);

        TransmissionProcessingResult climbReadback = await SayAsync(
            "climb flight level one two zero storm one two three");

        Assert.Equal(ReadbackOutcome.Correct, climbReadback.ReadbackAssessment?.Outcome);
        Assert.Null(_context.CurrentSession.PendingReadback);

        await AdvanceSimulatorToAsync(MockFlightStage.Cruise);

        // --- 10. Descent -------------------------------------------------------------------------------
        await AdvanceSimulatorToAsync(MockFlightStage.Descending);

        TransmissionProcessingResult descent = await SayAsync("storm one two three request descent");

        Assert.True(descent.Decision?.Accepted, descent.Decision?.RejectionReason ?? "no decision");
        Assert.Equal(ClearanceType.Descent, descent.Decision?.IssuedClearance?.Type);
        Assert.Equal(FlightPhase.Descent, _context.CurrentSession.Phase);
        AssertNoInventedValues(descent);

        Altitude descentLevel = descent.Decision!.IssuedClearance!.InitialAltitude!.Value;
        await SayAsync($"descend {descentLevel.ToSpoken()} storm one two three");
        Assert.Null(_context.CurrentSession.PendingReadback);

        // --- 11. Handed to Approach on entering the terminal area ---------------------------------------
        await AdvanceSimulatorToAsync(MockFlightStage.OnApproach);

        Assert.Equal(ControllerPosition.Approach, _context.CurrentSession.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(120.900m), _context.CurrentSession.CurrentFrequency);

        // --- 12. Approach clearance, which is not a landing clearance -------------------------------------
        TransmissionProcessingResult approach = await SayAsync("storm one two three request approach");

        Clearance approachClearance = Assert.IsType<Clearance>(approach.Decision?.IssuedClearance);
        Assert.Equal(ClearanceType.ApproachClearance, approachClearance.Type);
        Assert.Equal(RunwayIdentifier.Create("36"), approachClearance.Runway);
        Assert.DoesNotContain(
            "cleared to land",
            approach.ControllerSpokenText ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        AssertNoInventedValues(approach);

        await SayAsync(
            $"cleared {approachClearance.ApproachName} runway three six approach storm one two three");

        // --- 13. Established, handed to Tower, then cleared to land ----------------------------------------
        await SayAsync("storm one two three established on the localizer runway three six");

        Assert.Equal(ControllerPosition.Tower, _context.CurrentSession.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(118.700m), _context.CurrentSession.CurrentFrequency);
        Assert.Equal(FlightPhase.Final, _context.CurrentSession.Phase);

        TransmissionProcessingResult landing = await SayAsync(
            "storm one two three established on the localizer");

        Clearance landingClearance = Assert.IsType<Clearance>(landing.Decision?.IssuedClearance);
        Assert.Equal(ClearanceType.LandingClearance, landingClearance.Type);
        AssertNoInventedValues(landing);

        await SayAsync("runway three six cleared to land storm one two three");

        // --- 14. Landing and vacating ------------------------------------------------------------------------
        await AdvanceSimulatorToAsync(MockFlightStage.ShortFinal);
        await AdvanceSimulatorToAsync(MockFlightStage.Landed);

        Assert.Equal(FlightPhase.Landed, _context.CurrentSession.Phase);

        await SayAsync("storm one two three runway vacated");
        await SayAsync("storm one two three request taxi to stand");

        await AdvanceSimulatorToAsync(MockFlightStage.TaxiingIn);
        await AdvanceSimulatorToAsync(MockFlightStage.Parked);

        // --- 12. Session complete -------------------------------------------------------------------------
        Assert.Equal(FlightPhase.Parked, _context.CurrentSession.Phase);
        Assert.True(_context.CurrentSession.IsComplete);

        // --- Whole-flight assertions ---------------------------------------------------------------------
        AssertValidPhaseProgression();
        AssertControllerProgression();
        await AssertEverythingWasPersistedAsync();
    }

    // =====================================================================================
    // Assertions over the whole flight
    // =====================================================================================

    /// <summary>
    /// The session must have moved forwards through the phases, never sideways into one that
    /// cannot follow.
    /// </summary>
    private void AssertValidPhaseProgression()
    {
        Assert.Contains(FlightPhase.IFRClearanceIssued, _phaseHistory);
        Assert.Contains(FlightPhase.Pushback, _phaseHistory);
        Assert.Contains(FlightPhase.TaxiOut, _phaseHistory);
        Assert.Contains(FlightPhase.HoldingShort, _phaseHistory);
        Assert.Contains(FlightPhase.Takeoff, _phaseHistory);
        Assert.Contains(FlightPhase.InitialClimb, _phaseHistory);
        Assert.Contains(FlightPhase.Landed, _phaseHistory);
        Assert.Contains(FlightPhase.Parked, _phaseHistory);

        // Ordering: nothing may precede its prerequisite.
        AssertOrdered(FlightPhase.IFRClearanceIssued, FlightPhase.Pushback);
        AssertOrdered(FlightPhase.Pushback, FlightPhase.TaxiOut);
        AssertOrdered(FlightPhase.TaxiOut, FlightPhase.HoldingShort);
        AssertOrdered(FlightPhase.HoldingShort, FlightPhase.Takeoff);
        AssertOrdered(FlightPhase.Takeoff, FlightPhase.InitialClimb);
        AssertOrdered(FlightPhase.InitialClimb, FlightPhase.Landed);
        AssertOrdered(FlightPhase.Landed, FlightPhase.Parked);

        // Preflight is where it starts and Parked is where it ends.
        Assert.Equal(FlightPhase.Preflight, _phaseHistory[0]);
        Assert.Equal(FlightPhase.Parked, _phaseHistory[^1]);

        void AssertOrdered(FlightPhase earlier, FlightPhase later) =>
            Assert.True(
                _phaseHistory.IndexOf(earlier) < _phaseHistory.IndexOf(later),
                $"{earlier} should precede {later} but the sequence was "
                + string.Join(" → ", _phaseHistory));
    }

    /// <summary>Controllers must be worked in the order a real flight works them.</summary>
    private void AssertControllerProgression()
    {
        List<ControllerPosition> distinct = [.. _controllerHistory.Distinct()];

        Assert.Contains(ControllerPosition.ClearanceDelivery, distinct);
        Assert.Contains(ControllerPosition.Ground, distinct);
        Assert.Contains(ControllerPosition.Tower, distinct);
        Assert.Contains(ControllerPosition.Departure, distinct);
        Assert.Contains(ControllerPosition.Approach, distinct);

        Assert.True(
            _controllerHistory.IndexOf(ControllerPosition.ClearanceDelivery)
                < _controllerHistory.IndexOf(ControllerPosition.Ground),
            "Delivery must come before Ground.");

        Assert.True(
            _controllerHistory.IndexOf(ControllerPosition.Ground)
                < _controllerHistory.IndexOf(ControllerPosition.Tower),
            "Ground must come before Tower.");

        Assert.True(
            _controllerHistory.IndexOf(ControllerPosition.Tower)
                < _controllerHistory.IndexOf(ControllerPosition.Departure),
            "Tower must come before Departure.");

        // Every handoff the session recorded names a frequency inside the aeronautical band.
        foreach (ControllerHandoff handoff in _context.CurrentSession.HandoffHistory)
        {
            Assert.InRange(
                handoff.ToFrequency.Megahertz,
                Frequency.MinimumMegahertz,
                Frequency.MaximumMegahertz);
        }
    }

    /// <summary>
    /// Checks that the controller spoke only values the engine approved.
    /// </summary>
    /// <remarks>
    /// The same gate the language model would face, applied to the deterministic output. If the
    /// engine's own phraseology contained a value it had not approved, the validator that
    /// protects against a hallucinating model would be validating against a lie.
    /// </remarks>
    private static void AssertNoInventedValues(TransmissionProcessingResult result)
    {
        if (result.Decision is not AtcDecision decision
            || result.ControllerSpokenText is not { Length: > 0 } spoken)
        {
            return;
        }

        PhraseologyValidationResult validation = new PhraseologyValidator().Validate(spoken, decision);

        Assert.True(
            validation.IsValid,
            $"The controller said something the decision did not authorise: "
            + $"{string.Join("; ", validation.Problems)}\nSpoken: {spoken}");
    }

    private async Task AssertEverythingWasPersistedAsync()
    {
        IReadOnlyList<SessionSummary> sessions =
            await _repository.ListSessionsAsync(10, CancellationToken.None);

        SessionSummary summary = Assert.Single(sessions);
        Assert.Equal("STORM123", summary.Callsign);
        Assert.Equal("STMR", summary.Departure);
        Assert.Equal("STMD", summary.Destination);
        Assert.Equal("Mock", summary.Mode);

        IReadOnlyList<ConversationTurn> conversation =
            await _repository.LoadConversationAsync(summary.SessionId, CancellationToken.None);

        Assert.Contains(conversation, turn => turn.Speaker == ConversationSpeaker.Pilot);
        Assert.Contains(conversation, turn => turn.Speaker == ConversationSpeaker.Controller);

        // The whole session ran without a language model, so every controller turn is the
        // deterministic one — and is marked as such.
        Assert.All(
            conversation.Where(turn => turn.Speaker == ConversationSpeaker.Controller),
            turn => Assert.True(turn.UsedDeterministicFallback));
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private async Task<TransmissionProcessingResult> SayAsync(string text)
    {
        _clock.Advance(TimeSpan.FromSeconds(5));

        return await _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);
    }

    /// <summary>
    /// Moves the mock aircraft to a stage and lets the engine observe the new physical state.
    /// </summary>
    private async Task AdvanceSimulatorToAsync(MockFlightStage stage)
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        _simulator.SetStage(stage);
        _context.UpdateAircraftState(_simulator.GetCurrentState());

        await _coordinator.ProcessStateChangeAsync(CancellationToken.None);
    }

    /// <summary>Renders a taxiway letter the way a pilot reads it back.</summary>
    private static string SpokenTaxiway(string letter) => letter.ToUpperInvariant() switch
    {
        "A" => "alfa",
        "B" => "bravo",
        "C" => "charlie",
        "D" => "delta",
        "E" => "echo",
        "F" => "foxtrot",
        "G" => "golf",
        _ => letter,
    };

    /// <summary>A clock the test advances explicitly, so nothing depends on wall time.</summary>
    private sealed class TestClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan amount) => UtcNow += amount;
    }
}
