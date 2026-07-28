using Microsoft.Extensions.Logging.Abstractions;
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
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// The pipeline that turns one pilot transmission into one controller response.
/// </summary>
/// <remarks>
/// The properties tested here are the ones a user would notice going wrong: a transmission
/// processed twice, a response that came from an unvalidated model, or a session that stopped
/// working because a provider went down.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public class PilotTransmissionCoordinatorTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly StubAirportDataProvider _airports = new();
    private readonly StubTrafficProvider _traffic = new();
    private readonly StubWeatherProvider _weather = new();
    private readonly RecordingSessionRepository _repository = new();

    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;
    private StubLanguageModelRenderer? _modelRenderer;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        Build();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    /// <summary>Builds the pipeline, optionally with a language-model renderer attached.</summary>
    private void Build(StubLanguageModelRenderer? modelRenderer = null)
    {
        _modelRenderer = modelRenderer;

        _context = new AtcSessionContext(
            _airports, _traffic, _weather, _clock, NullLogger<AtcSessionContext>.Instance);

        _coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            _context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance,
            _repository,
            modelRenderer,
            new PhraseologyValidator());

        _context.PreassignedSquawk = SquawkCode.Create("4271");
        _context.UpdateSession(StartingSession());
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

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    // =====================================================================================

    [Fact]
    public async Task A_loaded_flight_starts_on_receive_only_atis_then_delivery_tuning_opens_the_radio()
    {
        AtcSession fresh = _context.StartNewSession(
            SessionMode.Mock,
            TestAerodromes.AtStand(_clock.UtcNow));
        FlightPlan plan = StartingSession().FlightPlan!;

        await _context.LoadFlightPlanAsync(plan, CancellationToken.None);

        Assert.Equal(ControllerPosition.Atis, _context.CurrentSession.CurrentController);
        Assert.Equal(
            Frequency.FromMegahertz(132.150m),
            _context.CurrentSession.CurrentFrequency);

        TransmissionProcessingResult onAtis = await SendAsync(
            "storm one two three request IFR clearance");

        Assert.False(onAtis.Processed);
        Assert.True(onAtis.WasIgnoredOnAtis);
        Assert.Null(onAtis.Intent);
        Assert.Null(onAtis.Decision);
        Assert.Empty(_context.CurrentSession.Conversation);

        AircraftState monitoringAtis = fresh.AircraftState with
        {
            Radio = fresh.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(132.150m),
                TransmitOnCom1 = true,
            },
        };
        _context.UpdateAircraftState(monitoringAtis);

        AircraftState onDelivery = monitoringAtis with
        {
            Radio = monitoringAtis.Radio with
            {
                Com1Active = Frequency.FromMegahertz(121.800m),
                TransmitOnCom1 = true,
            },
        };
        _context.UpdateAircraftState(onDelivery);

        Assert.Equal(ControllerPosition.ClearanceDelivery, _context.CurrentSession.CurrentController);
        Assert.Equal(
            Frequency.FromMegahertz(121.800m),
            _context.CurrentSession.CurrentFrequency);

        TransmissionProcessingResult heard = await SendAsync(
            "storm one two three request IFR clearance");

        Assert.True(heard.Processed);
        Assert.NotNull(heard.Decision?.IssuedClearance);
    }

    [Fact]
    public async Task An_off_frequency_transmission_is_discarded_before_parsing_or_transcript_storage()
    {
        AtcSession session = _context.CurrentSession;
        _context.UpdateAircraftState(session.AircraftState with
        {
            Radio = session.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(121.700m),
                TransmitOnCom1 = true,
            },
        });

        int conversationCount = _context.CurrentSession.Conversation.Count;
        TransmissionProcessingResult result = await SendAsync(
            "storm one two three request IFR clearance");

        Assert.False(result.Processed);
        Assert.True(result.WasIgnoredOffFrequency);
        Assert.Equal(Frequency.FromMegahertz(121.800m), result.ExpectedFrequency);
        Assert.Equal(Frequency.FromMegahertz(121.700m), result.TunedFrequency);
        Assert.Null(result.Intent);
        Assert.Null(result.Decision);
        Assert.Null(result.ControllerSpokenText);
        Assert.Equal(conversationCount, _context.CurrentSession.Conversation.Count);
        Assert.Empty(_repository.SavedTurns);
        Assert.Empty(_repository.SavedDecisions);
    }

    [Fact]
    public async Task An_airport_without_atis_starts_directly_on_its_delivery_or_combined_frequency()
    {
        AirportData withoutAtis = TestAerodromes.Departure with
        {
            Frequencies = TestAerodromes.Departure.Frequencies with { Atis = null },
        };
        var context = new AtcSessionContext(
            new StubAirportDataProvider(departureOverride: withoutAtis),
            _traffic,
            _weather,
            _clock,
            NullLogger<AtcSessionContext>.Instance);

        context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(_clock.UtcNow));
        AtcSession session = await context.LoadFlightPlanAsync(
            StartingSession().FlightPlan!,
            CancellationToken.None);

        Assert.Equal(ControllerPosition.ClearanceDelivery, session.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(121.800m), session.CurrentFrequency);
    }

    [Fact]
    public async Task A_clearance_request_produces_a_clearance_and_a_spoken_response()
    {
        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Processed);
        Assert.Equal(PilotIntentType.RequestIFRClearance, result.Intent?.Type);
        Assert.NotNull(result.Decision?.IssuedClearance);
        Assert.False(string.IsNullOrWhiteSpace(result.ControllerSpokenText));
        Assert.Contains("four two seven one", result.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_session_reflects_exactly_what_the_engine_approved()
    {
        await SendAsync("storm one two three request ifr clearance");

        AtcSession session = _context.CurrentSession;

        Assert.NotNull(session.IfrClearance);
        Assert.Equal(SquawkCode.Create("4271"), session.IfrClearance.Squawk);
        Assert.NotNull(session.PendingReadback);
        Assert.Equal(FlightPhase.IFRClearanceIssued, session.Phase);
    }

    [Fact]
    public async Task The_same_transmission_identifier_is_processed_once()
    {
        var transmission = PilotTransmission.Create(
            _context.CurrentSession.SessionId,
            "storm one two three request ifr clearance",
            TransmissionSource.ManualText,
            _clock.UtcNow);

        TransmissionProcessingResult first = await _coordinator.ProcessAsync(transmission, CancellationToken.None);
        TransmissionProcessingResult second = await _coordinator.ProcessAsync(transmission, CancellationToken.None);

        Assert.True(first.Processed);
        Assert.False(second.Processed);
        Assert.True(second.WasDuplicate);

        // And crucially, only one clearance exists.
        Assert.Single(_repository.SavedClearances);
    }

    [Fact]
    public async Task Overlapping_transmissions_are_serialised()
    {
        // Two clearance requests fired together must not both derive a clearance from the same
        // starting state.
        Task<TransmissionProcessingResult> first = SendAsync("storm one two three request ifr clearance");
        Task<TransmissionProcessingResult> second = SendAsync("storm one two three request ifr clearance");

        await Task.WhenAll(first, second);

        Assert.Single(_repository.SavedClearances);
    }

    [Fact]
    public async Task Both_sides_of_the_exchange_are_recorded()
    {
        await SendAsync("storm one two three request ifr clearance");

        AtcSession session = _context.CurrentSession;

        Assert.Contains(session.Conversation, turn => turn.Speaker == ConversationSpeaker.Pilot);
        Assert.Contains(session.Conversation, turn => turn.Speaker == ConversationSpeaker.Controller);
        Assert.Equal(2, _repository.SavedTurns.Count);
        Assert.Single(_repository.SavedDecisions);
    }

    [Fact]
    public async Task A_full_readback_advances_the_session_to_ground()
    {
        await SendAsync("storm one two three request ifr clearance");

        TransmissionProcessingResult result = await SendAsync(
            "cleared to storm delta runway one eight climb seven thousand squawk four two seven one storm one two three");

        Assert.Equal(ReadbackOutcome.Correct, result.ReadbackAssessment?.Outcome);
        Assert.Equal(ControllerPosition.Ground, _context.CurrentSession.CurrentController);
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.Single(_repository.SavedHandoffs);
    }

    [Fact]
    public async Task A_wrong_readback_leaves_the_session_where_it_was()
    {
        await SendAsync("storm one two three request ifr clearance");

        TransmissionProcessingResult result = await SendAsync(
            "cleared to storm delta runway one eight climb seven thousand squawk four two seven two storm one two three");

        Assert.NotEqual(ReadbackOutcome.Correct, result.ReadbackAssessment?.Outcome);
        Assert.Equal(ControllerPosition.ClearanceDelivery, _context.CurrentSession.CurrentController);
        Assert.NotNull(_context.CurrentSession.PendingReadback);
    }

    [Fact]
    public async Task Partial_readback_corrections_accumulate_across_the_exact_ST123_exchange()
    {
        _context.PreassignedSquawk = SquawkCode.Create("5217");
        AtcSession original = _context.CurrentSession;
        _context.UpdateSession(original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("ST123"),
                DepartureIcao = "EGKK",
                DestinationIcao = "ENVA",
                DepartureRunway = RunwayIdentifier.Create("26L"),
                StandardInstrumentDeparture = "FRAN1M",
                Route = "FRAN1M flight planned route",
            },
            AssignedDepartureRunway = RunwayIdentifier.Create("26L"),
        });

        TransmissionProcessingResult clearance = await SendAsync(
            "Tango123, request IFR clearance.");
        Assert.NotNull(clearance.Decision?.IssuedClearance);

        TransmissionProcessingResult firstReadback = await SendAsync(
            "Echo November Victor Alfa FRAN1 departure, Runway 26 left, "
            + "initially seven thousand and squawk five two one seven.");

        Assert.Equal(ReadbackOutcome.Incomplete, firstReadback.ReadbackAssessment?.Outcome);
        PendingReadback afterFirst = Assert.IsType<PendingReadback>(
            _context.CurrentSession.PendingReadback);
        Assert.Contains(ReadbackItemType.Runway, afterFirst.SatisfiedItems);
        Assert.Contains(ReadbackItemType.InitialAltitude, afterFirst.SatisfiedItems);
        Assert.Contains(ReadbackItemType.Squawk, afterFirst.SatisfiedItems);
        Assert.DoesNotContain(ReadbackItemType.Callsign, afterFirst.SatisfiedItems);

        TransmissionProcessingResult correction = await SendAsync(
            "Sorry, Echo November Victor Alfa from one departure Runway 26 left "
            + "initially 7000AndSquawk5217 SierraTango123.");

        Assert.Equal(ReadbackOutcome.Correct, correction.ReadbackAssessment?.Outcome);
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.True(_context.CurrentSession.IfrClearance?.IsAcknowledged);
    }

    [Fact]
    public async Task Joined_clearance_fields_are_spaced_and_a_partial_correction_keeps_confirmed_items()
    {
        _context.PreassignedSquawk = SquawkCode.Create("2555");
        AtcSession original = _context.CurrentSession;
        _context.UpdateSession(original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("A21"),
                DestinationIcao = "LOWS",
                DepartureRunway = RunwayIdentifier.Create("10"),
                StandardInstrumentDeparture = "MITO3A",
                Route = "MITO3A flight planned route",
            },
            AssignedDepartureRunway = RunwayIdentifier.Create("10"),
        });

        await SendAsync("Alfa two one request IFR clearance.");

        TransmissionProcessingResult firstReadback = await SendAsync(
            "Runway20Climb7000Squawk2555 Departure frequency one one eight decimal eight alpha two one.");

        Assert.Equal(ReadbackOutcome.Incorrect, firstReadback.ReadbackAssessment?.Outcome);
        Assert.Contains(
            firstReadback.ReadbackAssessment!.IncorrectItems,
            item => item.Type == ReadbackItemType.Runway && item.ActualValue == "20");
        Assert.Contains(ReadbackItemType.InitialAltitude, firstReadback.ReadbackAssessment.CorrectItems);
        Assert.Contains(ReadbackItemType.Squawk, firstReadback.ReadbackAssessment.CorrectItems);
        Assert.DoesNotContain(
            "climb initially",
            firstReadback.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "squawk",
            firstReadback.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);

        PendingReadback pending = Assert.IsType<PendingReadback>(
            _context.CurrentSession.PendingReadback);
        Assert.Contains(ReadbackItemType.InitialAltitude, pending.SatisfiedItems);
        Assert.Contains(ReadbackItemType.Squawk, pending.SatisfiedItems);

        ConversationTurn displayedPilotTurn = _context.CurrentSession.Conversation
            .Last(turn => turn.Speaker == ConversationSpeaker.Pilot);
        Assert.StartsWith(
            "Runway 20 Climb 7000 Squawk 2555",
            displayedPilotTurn.Text,
            StringComparison.Ordinal);

        TransmissionProcessingResult correctedReadback = await SendAsync(
            "Runway 10 climb initial at 7000 Alpha 21.");

        Assert.Equal(ReadbackOutcome.Correct, correctedReadback.ReadbackAssessment?.Outcome);
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.True(_context.CurrentSession.IfrClearance?.IsAcknowledged);
    }

    [Theory]
    [InlineData("Via apron, Alfa six, hold short Runway 10 Alpha 21.")]
    [InlineData("Holding short Runway 10 via apron, Alfa six hold short Runway 10 Alpha 21.")]
    public async Task Apron_and_alphanumeric_taxiway_readback_is_accepted_on_the_first_attempt(
        string transmission)
    {
        Guid clearanceId = Guid.CreateVersion7();
        RunwayIdentifier runway = RunwayIdentifier.Create("10");
        var taxiClearance = new Clearance
        {
            ClearanceId = clearanceId,
            Type = ClearanceType.Taxi,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = _clock.UtcNow,
            Runway = runway,
            TaxiRoute = ["apron", "A6"],
            TaxiDestination = "holding point 10",
            HoldShortOfRunway = runway,
            RequiredReadbackItems =
            [
                ReadbackItemType.TaxiRoute,
                ReadbackItemType.HoldShortRunway,
            ],
        };
        var pending = new PendingReadback
        {
            ClearanceId = clearanceId,
            ClearanceType = ClearanceType.Taxi,
            IssuedAt = _clock.UtcNow,
            ExpectedItems =
            [
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.TaxiRoute,
                    ExpectedValue = "apron,A6",
                    SpokenValue = "via apron, Alfa six",
                    IsCritical = true,
                },
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.HoldShortRunway,
                    ExpectedValue = runway.ToComparisonValue(),
                    SpokenValue = "hold short runway one zero",
                    IsCritical = true,
                },
            ],
        };

        AtcSession original = _context.CurrentSession;
        AtcSession awaitingTaxiReadback = original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("A21"),
                DepartureRunway = runway,
            },
            AssignedDepartureRunway = runway,
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = Frequency.FromMegahertz(130.655m),
            Phase = FlightPhase.TaxiOut,
            AircraftState = original.AircraftState with
            {
                Radio = original.AircraftState.Radio with
                {
                    Com1Active = Frequency.FromMegahertz(130.655m),
                    TransmitOnCom1 = true,
                },
            },
        };
        awaitingTaxiReadback = awaitingTaxiReadback
            .WithClearance(taxiClearance)
            .WithPendingReadback(pending);
        _context.UpdateSession(awaitingTaxiReadback);

        TransmissionProcessingResult result = await SendAsync(transmission);

        Assert.True(
            result.ReadbackAssessment?.Outcome == ReadbackOutcome.Correct,
            DescribeReadback(result.ReadbackAssessment));
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.True(_context.CurrentSession.TaxiClearance?.IsAcknowledged);
    }

    [Fact]
    public async Task The_exact_full_ST123_readback_is_accepted_on_the_first_attempt()
    {
        _context.PreassignedSquawk = SquawkCode.Create("5217");
        AtcSession original = _context.CurrentSession;
        _context.UpdateSession(original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("ST123"),
                DepartureIcao = "EGKK",
                DestinationIcao = "ENVA",
                DepartureRunway = RunwayIdentifier.Create("26L"),
                StandardInstrumentDeparture = "FRAN1M",
                Route = "FRAN1M flight planned route",
            },
            AssignedDepartureRunway = RunwayIdentifier.Create("26L"),
        });

        await SendAsync("Sierra Tango one two three, request IFR clearance");

        TransmissionProcessingResult readback = await SendAsync(
            "Clear to Echo November Victor Alfa. FRAN1M departure, Runway 26 left, "
            + "climb 7,000, squawk five two one seven, Sierra Tango one two three.");

        Assert.True(
            readback.ReadbackAssessment?.Outcome == ReadbackOutcome.Correct,
            DescribeReadback(readback.ReadbackAssessment));
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.True(_context.CurrentSession.IfrClearance?.IsAcknowledged);
    }

    [Fact]
    public async Task The_exact_latest_ST123_readback_survives_point_and_numeric_formatting()
    {
        _context.PreassignedSquawk = SquawkCode.Create("5217");
        AtcSession original = _context.CurrentSession;
        _context.UpdateSession(original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("ST123"),
                DepartureIcao = "EGKK",
                DestinationIcao = "ENVA",
                DepartureRunway = RunwayIdentifier.Create("26L"),
                StandardInstrumentDeparture = "FRAN1M",
                Route = "FRAN1M flight planned route",
            },
            AssignedDepartureRunway = RunwayIdentifier.Create("26L"),
        });

        await SendAsync("Sierra Tango one two three, request IFR clearance");

        PendingReadback pending = Assert.IsType<PendingReadback>(
            _context.CurrentSession.PendingReadback);
        _context.UpdateSession(_context.CurrentSession.WithPendingReadback(pending with
        {
            ExpectedItems =
            [
                .. pending.ExpectedItems,
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.Frequency,
                    ExpectedValue = Frequency.FromMegahertz(121.700m).ToComparisonValue(),
                    SpokenValue = "departure frequency one two one decimal seven",
                    IsCritical = false,
                },
            ],
        }));

        TransmissionProcessingResult readback = await SendAsync(
            "SierraTango123 Clear to Echo November Victor Alfa, from one m departure "
            + "Runway 26 left, climb 7,000, squawk five two point seven, "
            + "departure frequency one two one decimal seven.");

        Assert.True(
            readback.ReadbackAssessment?.Outcome == ReadbackOutcome.Correct,
            DescribeReadback(readback.ReadbackAssessment));
        Assert.Empty(readback.ReadbackAssessment!.IncorrectItems);
        Assert.DoesNotContain(
            ReadbackItemType.InitialAltitude,
            readback.ReadbackAssessment.MissingItems);
        Assert.DoesNotContain(
            ReadbackItemType.Squawk,
            readback.ReadbackAssessment.MissingItems);
        Assert.Null(_context.CurrentSession.PendingReadback);
        Assert.True(_context.CurrentSession.IfrClearance?.IsAcknowledged);
    }

    [Fact]
    public async Task The_exact_ST123_preflight_conversation_handles_pushback_and_frequency()
    {
        _context.PreassignedSquawk = SquawkCode.Create("5217");
        AtcSession original = _context.CurrentSession;
        _context.UpdateSession(original with
        {
            FlightPlan = original.FlightPlan! with
            {
                Callsign = Callsign.Create("ST123"),
                DepartureIcao = "EGKK",
                DestinationIcao = "ENVA",
                DepartureRunway = RunwayIdentifier.Create("26L"),
                StandardInstrumentDeparture = "FRAN1M",
                Route = "FRAN1M flight planned route",
            },
            AssignedDepartureRunway = RunwayIdentifier.Create("26L"),
        });

        TransmissionProcessingResult clearance = await SendAsync(
            "Sierra Tango one two three, request pushback IFR clearance");
        Assert.Equal(PilotIntentType.RequestIFRClearance, clearance.Intent?.Type);
        Assert.NotNull(_context.CurrentSession.PendingReadback);
        Assert.Contains(
            "then request pushback",
            clearance.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);

        TransmissionProcessingResult readback = await SendAsync(
            "SierraTango123 clear to Echo November Victor from 1 M Departure "
            + "Runway 26 left climb initially 7,000, squawk five two one seven.");
        Assert.True(
            readback.ReadbackAssessment?.Outcome == ReadbackOutcome.Correct,
            DescribeReadback(readback.ReadbackAssessment));
        Assert.Equal(
            ControllerPosition.ClearanceDelivery,
            _context.CurrentSession.CurrentController);

        TransmissionProcessingResult pushback = await SendAsync(
            "Sierra Tango one two three request pushback and start.");
        Assert.Equal(PilotIntentType.RequestPushback, pushback.Intent?.Type);
        Assert.True(pushback.Decision?.Accepted);
        Assert.Equal(AtcResponseType.PushbackApproval, pushback.Decision?.ResponseType);
        Assert.Equal(FlightPhase.Pushback, _context.CurrentSession.Phase);
        Assert.DoesNotContain(
            "contact Ground",
            pushback.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);

        TransmissionProcessingResult frequency = await SendAsync(
            "Sorry, what's What's the frequency?");
        Assert.Equal(PilotIntentType.RequestFrequency, frequency.Intent?.Type);
        Assert.True(frequency.Decision?.Accepted);
        Assert.Contains(
            "remain this frequency",
            frequency.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_frequency_question_returns_the_published_controller_frequency()
    {
        TransmissionProcessingResult result = await SendAsync(
            "Storm one two three, Ground frequency please");

        Assert.Equal(PilotIntentType.RequestFrequency, result.Intent?.Type);
        Assert.True(result.Decision?.Accepted);
        Assert.Contains(
            "Ground frequency one two one decimal seven",
            result.ControllerSpokenText!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unrecognised_transmission_gets_say_again_and_changes_nothing()
    {
        TransmissionProcessingResult result = await SendAsync("the weather is lovely today");

        Assert.True(result.Processed);
        Assert.Null(_context.CurrentSession.IfrClearance);
        Assert.Equal(FlightPhase.Preflight, _context.CurrentSession.Phase);
        Assert.False(string.IsNullOrWhiteSpace(result.ControllerSpokenText));
    }

    // --- Language model gating --------------------------------------------------------------

    [Fact]
    public async Task Valid_model_phrasing_is_used()
    {
        // More conversational than the deterministic wording, but every mandatory fact is
        // present and every number is one the engine approved.
        var model = new StubLanguageModelRenderer
        {
            Response = "Storm one two three, you're cleared to Storm Delta via the Storm One Alfa, "
                + "runway one eight, climb initially seven thousand, squawk four two seven one, "
                + "departure frequency one one nine decimal one.",
        };

        Build(model);

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.Equal(1, model.CallCount);
        Assert.False(result.UsedDeterministicFallback);
        Assert.Equal(model.Response, result.ControllerSpokenText);
    }

    [Fact]
    public async Task Model_phrasing_that_omits_the_squawk_is_discarded()
    {
        // Fluent and entirely plausible, but a clearance without a transponder code is not a
        // clearance. Omission is as much a failure as a wrong value.
        var model = new StubLanguageModelRenderer
        {
            Response = "Storm one two three, cleared to Storm Delta, runway one eight, "
                + "climb initially seven thousand, departure frequency one one nine decimal one.",
        };

        Build(model);

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.UsedDeterministicFallback);
        Assert.Contains("four two seven one", result.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Model_phrasing_with_an_unapproved_altitude_is_discarded()
    {
        var model = new StubLanguageModelRenderer
        {
            Response = "Storm one two three, cleared to Storm Delta, runway one eight, "
                + "climb initially five thousand, squawk four two seven one.",
        };

        Build(model);

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.UsedDeterministicFallback);
        Assert.DoesNotContain("five thousand", result.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seven thousand", result.ControllerSpokenText!, StringComparison.OrdinalIgnoreCase);

        // The session still carries the engine's altitude, not the model's.
        Assert.Equal(7_000, _context.CurrentSession.IfrClearance?.InitialAltitude?.Feet);
    }

    [Fact]
    public async Task A_model_that_throws_falls_back_without_failing_the_transmission()
    {
        var model = new StubLanguageModelRenderer
        {
            Failure = new HttpRequestException("The provider is unreachable."),
        };

        Build(model);

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Processed);
        Assert.True(result.UsedDeterministicFallback);
        Assert.NotNull(result.Decision?.IssuedClearance);
    }

    [Fact]
    public async Task An_unavailable_model_is_never_called()
    {
        var model = new StubLanguageModelRenderer { Available = false, Response = "anything" };

        Build(model);

        await SendAsync("storm one two three request ifr clearance");

        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task The_model_is_not_consulted_for_a_correction()
    {
        var model = new StubLanguageModelRenderer { Response = "Roger." };

        Build(model);

        await SendAsync("storm one two three request ifr clearance");
        int afterClearance = model.CallCount;

        await SendAsync("squawk four two seven two storm one two three");

        Assert.Equal(afterClearance, model.CallCount);
    }

    // --- Degraded providers -------------------------------------------------------------------

    [Fact]
    public async Task A_traffic_provider_outage_does_not_stop_the_session()
    {
        _traffic.ShouldThrow = true;

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Processed);
        Assert.NotNull(result.Decision?.IssuedClearance);
    }

    [Fact]
    public async Task A_weather_provider_outage_does_not_stop_the_session()
    {
        _weather.ShouldThrow = true;

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Processed);
    }

    [Fact]
    public async Task A_database_outage_does_not_stop_the_session()
    {
        // Persistence is off the critical path: a broken database costs the transcript, not
        // the clearance.
        _repository.ShouldThrow = true;

        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Processed);
        Assert.NotNull(result.Decision?.IssuedClearance);
        Assert.NotNull(_context.CurrentSession.IfrClearance);
    }

    [Fact]
    public async Task Missing_airport_data_uses_the_filed_runway_without_guessing_local_details()
    {
        var context = new AtcSessionContext(
            new StubAirportDataProvider(shouldThrow: true),
            _traffic,
            _weather,
            _clock,
            NullLogger<AtcSessionContext>.Instance);

        await using var coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance);

        context.UpdateSession(StartingSession());

        TransmissionProcessingResult result = await coordinator.ProcessAsync(
            PilotTransmission.Create(
                context.CurrentSession.SessionId,
                "storm one two three request ifr clearance",
                TransmissionSource.ManualText,
                _clock.UtcNow),
            CancellationToken.None);

        Assert.True(result.Processed);
        Assert.True(result.Decision?.Accepted);
        Assert.Equal(
            RunwayIdentifier.Create("18"),
            result.Decision?.IssuedClearance?.Runway);
        Assert.Null(result.Decision?.IssuedClearance?.NextFrequency);
    }

    [Fact]
    public async Task Timings_are_recorded_for_the_diagnostics_page()
    {
        TransmissionProcessingResult result = await SendAsync("storm one two three request ifr clearance");

        Assert.True(result.Timings.Total > TimeSpan.Zero);
        Assert.True(result.Timings.Total >= result.Timings.EngineEvaluation);
    }

    [Fact]
    public async Task A_processed_transmission_raises_the_event_the_UI_listens_to()
    {
        TransmissionProcessedEventArgs? captured = null;
        _coordinator.TransmissionProcessed += (_, e) => captured = e;

        await SendAsync("storm one two three request ifr clearance");

        Assert.NotNull(captured);
        Assert.True(captured.Result.Processed);
    }

    private static string DescribeReadback(ReadbackAssessment? assessment) =>
        assessment is null
            ? "No readback assessment was produced."
            : $"Outcome={assessment.Outcome}; "
                + $"correct=[{string.Join(',', assessment.CorrectItems)}]; "
                + $"missing=[{string.Join(',', assessment.MissingItems)}]; "
                + $"incorrect=[{string.Join(',', assessment.IncorrectItems.Select(item =>
                    $"{item.Type}:{item.ActualValue}->{item.ExpectedValue}"))}]";
}
