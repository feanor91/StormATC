using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// The deterministic ATC engine: what it permits, what it refuses, and what it never invents.
/// </summary>
/// <remarks>
/// These are the tests that matter most. The engine is the only thing in StormATC that decides
/// whether a clearance exists, so every rule it enforces has to be pinned here rather than left
/// to a language model's judgement.
/// </remarks>
public class AtcStateEngineTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    /// <summary>The warning code the engine raises for a simulated hold-short violation.</summary>
    private const string HoldShortViolation = "HoldShortViolation";

    // =====================================================================================
    // Guards
    // =====================================================================================

    [Fact]
    public void No_intent_produces_a_silent_decision()
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(TestScenario.NewSession(), intent: null));

        Assert.Equal(AtcResponseType.None, decision.ResponseType);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void A_transmission_with_the_wrong_callsign_is_refused()
    {
        AtcSession session = TestScenario.NewSession();

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.RequestIFRClearance,
            "speedbird four five six request clearance",
            callsign: Callsign.Create("BAW456"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, intent));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
        Assert.NotNull(decision.RejectionReason);
    }

    [Fact]
    public void A_wrong_callsign_is_tolerated_on_the_relaxed_profile()
    {
        AtcSession session = TestScenario.NewSession();

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.RequestIFRClearance,
            "request clearance",
            callsign: Callsign.Create("BAW456"));

        AtcDecision decision = _engine.Evaluate(
            TestScenario.Context(session, intent, realism: RealismProfile.Relaxed));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void Realism_profiles_select_the_expected_transcript_tolerance()
    {
        Assert.Equal(
            TranscriptToleranceMode.Relaxed,
            RealismProfile.Relaxed.TranscriptTolerance);
        Assert.Equal(
            TranscriptToleranceMode.Standard,
            RealismProfile.Standard.TranscriptTolerance);
        Assert.Equal(
            TranscriptToleranceMode.Strict,
            RealismProfile.Strict.TranscriptTolerance);
    }

    [Fact]
    public void A_low_confidence_safety_critical_transmission_is_not_acted_on()
    {
        // "Ready for departure" mis-heard is how an aircraft gets a takeoff clearance it never
        // asked for, so below the confidence threshold the engine asks again instead of acting.
        AtcSession session = ReadyForDeparture();

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.ReadyForDeparture, "mumbled something", confidence: 0.2);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, intent));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
        Assert.Contains("say again", decision.FallbackPhraseology, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_low_confidence_ordinary_request_is_still_served()
    {
        // A clearance request is not safety-critical: acting on it produces a clearance the
        // pilot must then read back, so the readback catches a misunderstanding anyway.
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance", confidence: 0.35)));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void An_emergency_bypasses_every_other_guard()
    {
        // Wrong controller, outstanding readback, low confidence — none of it may delay a mayday.
        AtcSession session = TestScenario.NewSession(TestScenario.Airborne())
            with
        { CurrentController = ControllerPosition.Ground, Phase = FlightPhase.InitialClimb };

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.Mayday, "mayday mayday mayday storm one two three engine failure",
            confidence: 0.3);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, intent));

        Assert.True(decision.Accepted);
        Assert.Equal(AtcResponseType.EmergencyAcknowledgement, decision.ResponseType);
    }

    [Fact]
    public void An_outstanding_mandatory_readback_blocks_the_next_request()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession());

        Assert.NotNull(session.PendingReadback);

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.RequestPushback, "storm one two three request pushback");

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, intent));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void Requesting_the_wrong_controllers_service_is_refused()
    {
        // Still on delivery; a takeoff clearance is not delivery's to give.
        AtcSession session = TestScenario.NewSession(TestScenario.AtHoldingPoint());

        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.ReadyForDeparture, "storm one two three ready for departure");

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(session, intent));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
    }

    // =====================================================================================
    // IFR clearance
    // =====================================================================================

    [Fact]
    public void An_IFR_clearance_is_issued_with_every_mandatory_element()
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "storm one two three request ifr clearance")));

        Assert.True(decision.Accepted);
        Assert.NotNull(decision.IssuedClearance);

        Clearance clearance = decision.IssuedClearance;
        Assert.Equal(ClearanceType.IfrClearance, clearance.Type);
        Assert.Equal("STMD", clearance.ClearedToDestination);
        Assert.NotNull(clearance.Squawk);
        Assert.NotNull(clearance.InitialAltitude);
    }

    [Fact]
    public void A_filed_runway_allows_clearance_without_detailed_airport_data()
    {
        AtcEvaluationContext context = TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance"))
            with
        {
            DepartureAirport = null,
        };

        AtcDecision decision = _engine.Evaluate(context);

        Assert.True(decision.Accepted);
        Assert.Equal(RunwayIdentifier.Create("18"), decision.IssuedClearance?.Runway);
        Assert.Null(decision.IssuedClearance?.NextFrequency);
    }

    [Fact]
    public void A_preassigned_squawk_is_used_rather_than_a_generated_one()
    {
        var pinned = SquawkCode.Create("4271");

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance"),
            preassignedSquawk: pinned));

        Assert.Equal(pinned, decision.IssuedClearance?.Squawk);
    }

    [Fact]
    public void A_generated_squawk_is_a_valid_non_emergency_code()
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance")));

        SquawkCode? squawk = decision.IssuedClearance?.Squawk;

        Assert.NotNull(squawk);
        Assert.False(squawk.Value.IsEmergencyCode);
        Assert.True(SquawkCode.TryCreate(squawk.Value.Digits, out _));
    }

    [Fact]
    public void The_same_session_always_gets_the_same_generated_squawk()
    {
        // Reproducibility matters: re-evaluating must not silently reassign the transponder.
        AtcEvaluationContext context = TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance"));

        Assert.Equal(
            _engine.Evaluate(context).IssuedClearance?.Squawk,
            _engine.Evaluate(context).IssuedClearance?.Squawk);
    }

    [Fact]
    public void A_clearance_requires_a_readback_of_the_safety_critical_items()
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance")));

        Assert.NotNull(decision.RequiredReadback);

        IReadOnlyList<ReadbackItemType> required =
            [.. decision.RequiredReadback.ExpectedItems.Select(item => item.Type)];

        Assert.Contains(ReadbackItemType.Squawk, required);
        Assert.Contains(ReadbackItemType.InitialAltitude, required);
    }

    [Fact]
    public void Every_number_the_response_may_speak_is_declared_as_an_approved_fact()
    {
        // The phraseology validator rejects any spoken number absent from ApprovedFacts, so an
        // element the engine issued but did not declare would be unspeakable.
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance"),
            preassignedSquawk: SquawkCode.Create("4271")));

        Clearance clearance = Assert.IsType<Clearance>(decision.IssuedClearance);

        Assert.Contains("4271", decision.ApprovedFacts.Values);
        Assert.Contains(
            clearance.InitialAltitude!.Value.ToComparisonValue(),
            decision.ApprovedFacts.Values);
    }

    // =====================================================================================
    // Readback
    // =====================================================================================

    [Fact]
    public void A_correct_readback_clears_the_pending_item()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(
                PilotIntentType.ClearanceReadback,
                "cleared to storm delta runway one eight climb seven thousand squawk four two seven one storm one two three"),
            transcript:
                "cleared to storm delta runway one eight climb seven thousand squawk four two seven one storm one two three"));

        Assert.True(decision.Accepted);
        Assert.True(decision.ClearsPendingReadback);
        Assert.Equal(ReadbackOutcome.Correct, decision.ReadbackAssessment?.Outcome);
    }

    [Fact]
    public void A_wrong_squawk_in_the_readback_is_corrected_and_does_not_clear_it()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(
                PilotIntentType.ClearanceReadback,
                "runway one eight climb seven thousand squawk four two seven two storm one two three"),
            transcript:
                "runway one eight climb seven thousand squawk four two seven two storm one two three"));

        Assert.False(decision.ClearsPendingReadback);
        Assert.Equal(ReadbackOutcome.Incorrect, decision.ReadbackAssessment?.Outcome);

        // The correction must name the right value, not just say "negative". It is spoken from
        // the deterministic fallback, never handed to a model that could restate the wrong code.
        Assert.Contains(
            "four two seven one",
            decision.FallbackPhraseology,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            decision.ReadbackAssessment!.IncorrectItems,
            item => item.Type == ReadbackItemType.Squawk && item.ActualValue == "4272");
    }

    [Fact]
    public void Standard_profile_does_not_call_an_unannounced_parser_guess_wrong()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));
        const string Transcript =
            "runway one eight climb seven thousand storm one two three";
        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.ClearanceReadback,
            Transcript,
            readbackItems:
            [
                new ParsedReadbackItem
                {
                    Type = ReadbackItemType.Squawk,
                    NormalizedValue = "7000",
                    SourceText = Transcript,
                },
            ]);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            intent,
            transcript: Transcript,
            realism: RealismProfile.Standard));

        Assert.Equal(ReadbackOutcome.Incomplete, decision.ReadbackAssessment?.Outcome);
        Assert.Contains(
            ReadbackItemType.Squawk,
            decision.ReadbackAssessment!.MissingItems);
        Assert.Empty(decision.ReadbackAssessment.IncorrectItems);
        Assert.Contains(
            "readback incomplete",
            decision.FallbackPhraseology,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "negative",
            decision.FallbackPhraseology,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strict_profile_judges_the_same_structured_parser_value_as_intentional()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));
        const string Transcript =
            "runway one eight climb seven thousand storm one two three";
        PilotIntent intent = TestScenario.Intent(
            PilotIntentType.ClearanceReadback,
            Transcript,
            readbackItems:
            [
                new ParsedReadbackItem
                {
                    Type = ReadbackItemType.Squawk,
                    NormalizedValue = "7000",
                    SourceText = Transcript,
                },
            ]);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            intent,
            transcript: Transcript,
            realism: RealismProfile.Strict));

        Assert.Equal(ReadbackOutcome.Incorrect, decision.ReadbackAssessment?.Outcome);
        Assert.Contains(
            decision.ReadbackAssessment!.IncorrectItems,
            item => item.Type == ReadbackItemType.Squawk);
        Assert.Contains(
            "negative",
            decision.FallbackPhraseology,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relaxed_profile_accepts_a_complete_readback_without_the_callsign()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));
        const string Transcript =
            "runway one eight climb seven thousand squawk four two seven one";
        var intent = new PilotIntent
        {
            Type = PilotIntentType.ClearanceReadback,
            Confidence = 0.95,
            Entities = IntentEntities.Empty,
            Source = IntentParserSource.Deterministic,
            SourceTranscript = Transcript,
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            intent,
            transcript: Transcript,
            realism: RealismProfile.Relaxed));

        Assert.Equal(ReadbackOutcome.Correct, decision.ReadbackAssessment?.Outcome);
        Assert.True(decision.ClearsPendingReadback);
    }

    [Fact]
    public void An_incomplete_readback_asks_only_for_what_is_missing()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ClearanceReadback, "runway one eight storm one two three"),
            transcript: "runway one eight storm one two three"));

        Assert.False(decision.ClearsPendingReadback);
        Assert.NotNull(decision.ReadbackAssessment);
        Assert.NotEmpty(decision.ReadbackAssessment.MissingItems);
    }

    [Fact]
    public void A_correction_response_is_never_handed_to_the_language_model()
    {
        // Exact wording matters more than natural delivery when correcting a pilot.
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ClearanceReadback, "squawk four two seven two"),
            transcript: "squawk four two seven two"));

        Assert.False(decision.AllowLanguageModelPhraseology);
    }

    [Fact]
    public void Roger_does_not_bypass_an_outstanding_mandatory_readback()
    {
        AtcSession session = IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger storm one two three")));

        Assert.False(decision.Accepted);
        Assert.False(decision.ClearsPendingReadback);
        Assert.Contains("read back", decision.FallbackPhraseology, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // Ground
    // =====================================================================================

    [Fact]
    public void Pushback_is_approved_once_the_clearance_is_read_back()
    {
        AtcSession session = AcknowledgedClearance();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session with { CurrentController = ControllerPosition.Ground, CurrentFrequency = TestScenario.Ground },
            TestScenario.Intent(PilotIntentType.RequestPushback, "storm one two three request pushback")));

        Assert.True(decision.Accepted);
        Assert.Equal(FlightPhase.Pushback, decision.NewPhase);
    }

    [Fact]
    public void Pushback_approval_repeats_the_requested_tail_direction()
    {
        AtcSession session = AcknowledgedClearance();
        var entities = new IntentEntities { PushbackDirection = "tail right" };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session with
            {
                CurrentController = ControllerPosition.Ground,
                CurrentFrequency = TestScenario.Ground,
            },
            TestScenario.Intent(
                PilotIntentType.RequestPushback,
                "storm one two three request pushback tail right",
                entities: entities)));

        Assert.True(decision.Accepted);
        Assert.Equal("tail right", decision.ApprovedFacts["pushbackDirection"]);
        Assert.Contains("pushback approved", decision.FallbackPhraseology);
        Assert.Contains("tail right", decision.FallbackPhraseology);
    }

    [Fact]
    public void Pushback_without_a_requested_direction_uses_the_configured_GSX_default()
    {
        AtcSession session = AcknowledgedClearance();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session with
            {
                CurrentController = ControllerPosition.Ground,
                CurrentFrequency = TestScenario.Ground,
            },
            TestScenario.Intent(
                PilotIntentType.RequestPushback,
                "storm one two three request pushback"),
            defaultPushbackDirection: PushbackDirections.TailLeft));

        Assert.Equal("tail left", decision.ApprovedFacts["pushbackDirection"]);
        Assert.Contains("tail left", decision.FallbackPhraseology);
        Assert.DoesNotContain("east", decision.FallbackPhraseology);
    }

    [Fact]
    public void A_taxi_clearance_routes_via_real_taxiways_and_ends_at_a_holding_point()
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "storm one two three request taxi")));

        Assert.True(decision.Accepted);

        Clearance clearance = Assert.IsType<Clearance>(decision.IssuedClearance);
        Assert.Equal(ClearanceType.Taxi, clearance.Type);
        Assert.NotEmpty(clearance.TaxiRoute);
        Assert.NotEmpty(clearance.TaxiPathNodes);
        Assert.True(clearance.TaxiRouteLengthMeters > 0);
        Assert.Equal(RunwayIdentifier.Create("18"), clearance.HoldShortOfRunway);

        // The route must be taxiways that exist at this aerodrome.
        Assert.All(clearance.TaxiRoute, taxiway => Assert.Contains(taxiway, new[] { "A", "B", "C", "D" }));
    }

    [Fact]
    public void Taxi_clearance_uses_live_aircraft_position_even_if_an_old_stand_is_assigned()
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
            AssignedStand = "A1",
        };
        GeoPosition standA2 = TestScenario.Departure_STMR.TaxiGraph!.FindNode("S_A2")!.Position;
        session = session with
        {
            AircraftState = session.AircraftState with { Position = standA2 },
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi")));

        Clearance clearance = Assert.IsType<Clearance>(decision.IssuedClearance);
        Assert.Equal("S_A2", clearance.TaxiPathNodes[0]);
        Assert.NotEqual("S_A1", clearance.TaxiPathNodes[0]);
    }

    [Fact]
    public void Progressive_taxi_gives_the_next_graph_derived_step()
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
        };
        AtcDecision taxiDecision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi")));
        Clearance taxiClearance = Assert.IsType<Clearance>(taxiDecision.IssuedClearance)
            with { AcknowledgedAt = TestScenario.Now };
        session = session.WithClearance(taxiClearance) with
        {
            Phase = FlightPhase.TaxiOut,
            PendingReadback = null,
        };

        AtcDecision progressive = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(
                PilotIntentType.RequestProgressiveTaxi,
                "request progressive taxi")));

        Assert.True(progressive.Accepted);
        Assert.False(progressive.AllowLanguageModelPhraseology);
        Assert.Contains("continue on Alfa", progressive.FallbackPhraseology);
        Assert.Contains("Charlie", progressive.FallbackPhraseology);
        Assert.Contains("next junction", progressive.FallbackPhraseology);
    }

    [Fact]
    public void A_taxi_clearance_requires_the_hold_short_instruction_to_be_read_back()
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi")));

        Assert.NotNull(decision.RequiredReadback);
        Assert.Contains(
            decision.RequiredReadback.ExpectedItems,
            item => item.Type == ReadbackItemType.HoldShortRunway);
    }

    [Fact]
    public void A_filed_runway_allows_limited_taxi_without_detailed_airport_data()
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
        };

        AtcEvaluationContext context = TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi"))
            with
        {
            DepartureAirport = null,
        };

        AtcDecision decision = _engine.Evaluate(context);

        Assert.True(decision.Accepted);
        Clearance clearance = Assert.IsType<Clearance>(decision.IssuedClearance);
        Assert.Empty(clearance.TaxiRoute);
        Assert.Equal(RunwayIdentifier.Create("18"), clearance.HoldShortOfRunway);
        Assert.Contains(
            decision.RequiredReadback!.ExpectedItems,
            item => item.Type == ReadbackItemType.HoldShortRunway);
        Assert.Contains(
            "routing unavailable",
            decision.FallbackPhraseology,
            StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // Tower
    // =====================================================================================

    [Fact]
    public void Takeoff_clearance_is_issued_at_the_holding_point_with_a_clear_runway()
    {
        AtcSession session = ReadyForDeparture();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "storm one two three ready for departure")));

        Assert.True(decision.Accepted);
        Assert.Equal(AtcResponseType.TakeoffClearance, decision.ResponseType);
        Assert.Equal(ClearanceType.Takeoff, decision.IssuedClearance?.Type);
    }

    [Fact]
    public void Takeoff_clearance_is_refused_while_the_runway_is_occupied()
    {
        AtcSession session = ReadyForDeparture();

        var traffic = new List<TrafficContact>
        {
            new()
            {
                Callsign = "MOCK001",
                Position = TestScenario.Runway18Midpoint,
                AltitudeFeet = 0,
                IsOnRunway = true,
                OccupiedRunway = RunwayIdentifier.Create("18"),
            },
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure"),
            traffic: traffic));

        Assert.False(decision.Accepted);
        Assert.NotEqual(ClearanceType.Takeoff, decision.IssuedClearance?.Type);
        Assert.NotEqual(AtcResponseType.TakeoffClearance, decision.ResponseType);
    }

    [Fact]
    public void Takeoff_clearance_is_refused_before_the_aircraft_has_taxied()
    {
        // The phase, not the coordinate, is what the engine trusts here — and the phase only
        // reaches the holding point through the taxi flow.
        AtcSession session = ReadyForDeparture() with
        {
            Phase = FlightPhase.Preflight,
            AircraftState = TestScenario.AtStand(),
        };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure")));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void Takeoff_clearance_is_refused_when_the_aircraft_is_already_airborne()
    {
        AtcSession session = ReadyForDeparture() with { AircraftState = TestScenario.Airborne() };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure")));

        Assert.False(decision.Accepted);
        Assert.Null(decision.IssuedClearance);
    }

    [Fact]
    public void Wind_is_omitted_when_there_is_no_weather_source()
    {
        // A null weather snapshot means "no source". Inventing a wind check would be a
        // fabricated fact, which is exactly what the engine must never do.
        AtcDecision withoutWeather = _engine.Evaluate(TestScenario.Context(
            ReadyForDeparture(),
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure"),
            weather: null));

        Assert.DoesNotContain("windDirection", withoutWeather.ApprovedFacts.Keys);

        AtcDecision withWeather = _engine.Evaluate(TestScenario.Context(
            ReadyForDeparture(),
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure"),
            weather: new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(190),
                WindSpeedKnots = 8,
            }));

        Assert.Contains("windDirection", withWeather.ApprovedFacts.Keys);
    }

    // =====================================================================================
    // Airborne
    // =====================================================================================

    [Fact]
    public void Becoming_airborne_moves_the_session_into_the_initial_climb()
    {
        AtcSession session = ReadyForDeparture() with
        {
            Phase = FlightPhase.Takeoff,
            AircraftState = TestScenario.Airborne(1_200),
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.Equal(FlightPhase.InitialClimb, decision.NewPhase);

        // Nothing is said. The pilot is busy; a transmission here would be noise.
        Assert.Equal(AtcResponseType.None, decision.ResponseType);
    }

    [Fact]
    public void A_stable_climb_through_a_thousand_feet_hands_off_to_departure()
    {
        AtcSession session = ReadyForDeparture() with
        {
            Phase = FlightPhase.InitialClimb,
            AircraftState = TestScenario.Airborne(1_500),
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.Equal(ControllerPosition.Departure, decision.Handoff?.ToController);
        Assert.Equal(TestScenario.Departure, decision.Handoff?.ToFrequency);
    }

    [Fact]
    public void No_handoff_is_issued_before_the_aircraft_is_established_in_the_climb()
    {
        AtcSession session = ReadyForDeparture() with
        {
            Phase = FlightPhase.InitialClimb,
            AircraftState = TestScenario.Airborne(600),
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.Null(decision.Handoff);
    }

    [Fact]
    public void A_climb_request_above_the_planned_cruise_is_not_granted_as_asked()
    {
        AtcSession session = EnRoute();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(
                PilotIntentType.RequestClimb,
                "storm one two three request flight level four zero zero",
                entities: new IntentEntities { Altitude = Altitude.FromFlightLevel(400) })));

        Assert.NotEqual(
            Altitude.FromFlightLevel(400).ToComparisonValue(),
            decision.IssuedClearance?.CruiseAltitude?.ToComparisonValue());
    }

    [Fact]
    public void A_climb_to_the_planned_cruise_is_granted()
    {
        AtcSession session = EnRoute();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(
                PilotIntentType.RequestClimb,
                "request climb flight level one two zero",
                entities: new IntentEntities { Altitude = Altitude.FromFlightLevel(120) })));

        Assert.True(decision.Accepted);
        Assert.Contains(
            Altitude.FromFlightLevel(120).ToComparisonValue(),
            decision.ApprovedFacts.Values);
    }

    // =====================================================================================
    // Arrival
    // =====================================================================================

    [Fact]
    public void An_approach_clearance_is_not_a_landing_clearance()
    {
        AtcSession session = OnApproach();

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestApproach, "storm one two three request approach")));

        Assert.True(decision.Accepted);
        Assert.Equal(ClearanceType.ApproachClearance, decision.IssuedClearance?.Type);
        Assert.NotEqual(ClearanceType.LandingClearance, decision.IssuedClearance?.Type);
        Assert.Contains("cleared to land", decision.ProhibitedFacts);
    }

    [Fact]
    public void A_go_around_cancels_the_landing_clearance_and_is_always_accepted()
    {
        AtcSession session = OnApproach() with { Phase = FlightPhase.Final };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.GoAround, "storm one two three going around", confidence: 0.4)));

        Assert.True(decision.Accepted);
        Assert.Equal(AtcResponseType.GoAroundInstruction, decision.ResponseType);
        Assert.Equal(FlightPhase.GoAround, decision.NewPhase);
    }

    [Fact]
    public void Reaching_the_stand_completes_the_session()
    {
        AtcSession session = TestScenario.NewSession(TestScenario.AtStand()) with
        {
            Phase = FlightPhase.TaxiIn,
            CurrentController = ControllerPosition.Ground,
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.Equal(FlightPhase.Parked, decision.NewPhase);
    }

    // =====================================================================================
    // Physical state
    // =====================================================================================

    [Fact]
    public void Entering_the_runway_against_a_hold_short_instruction_raises_a_warning()
    {
        AtcSession session = TaxiingWithHoldShort(TestScenario.Runway18Midpoint);

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.NotEmpty(decision.Warnings);
        Assert.Contains(decision.Warnings, warning => warning.Severity >= AtcSeverity.Warning);
        Assert.Contains(decision.Warnings, warning => warning.Code == HoldShortViolation);

        // This is simulation logic, not a real-world protection, and the message says so.
        Assert.Contains(
            "Simulated",
            decision.Warnings[0].Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Taxiing_clear_of_the_runway_raises_nothing()
    {
        AtcSession session = TaxiingWithHoldShort(TestScenario.HoldingPoint18);

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.DoesNotContain(decision.Warnings, warning => warning.Code == HoldShortViolation);
    }

    [Fact]
    public void The_same_violation_is_not_warned_about_twice()
    {
        // A warning per simulator update — five a second — would bury everything else.
        AtcSession session = TaxiingWithHoldShort(TestScenario.Runway18Midpoint);

        AtcDecision first = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));
        AtcSession afterWarning = session.WithWarning(first.Warnings[0]);

        AtcDecision second = _engine.EvaluateStateChange(
            TestScenario.Context(afterWarning, intent: null));

        Assert.DoesNotContain(second.Warnings, warning => warning.Code == HoldShortViolation);
    }

    [Fact]
    public void Entering_the_runway_with_a_takeoff_clearance_raises_nothing()
    {
        AtcSession cleared = ReadyForDeparture();

        AtcDecision takeoff = _engine.Evaluate(TestScenario.Context(
            cleared,
            TestScenario.Intent(PilotIntentType.ReadyForDeparture, "ready for departure")));

        AtcSession session = cleared
            .WithClearance(Assert.IsType<Clearance>(takeoff.IssuedClearance))
            .WithReadbackAccepted(TestScenario.Now)
            with
        {
            Phase = FlightPhase.Takeoff,
            AircraftState = TestScenario.AtStand() with
            {
                Position = TestScenario.Runway18Midpoint,
                GroundSpeedKnots = 60,
                ParkingBrakeSet = false,
                EnginesRunning = true,
            },
        };

        AtcDecision decision = _engine.EvaluateStateChange(TestScenario.Context(session, intent: null));

        Assert.DoesNotContain(decision.Warnings, warning => warning.Code == HoldShortViolation);
    }

    [Fact]
    public void Hold_short_enforcement_can_be_switched_off()
    {
        AtcSession session = TaxiingWithHoldShort(TestScenario.Runway18Midpoint);

        AtcDecision decision = _engine.EvaluateStateChange(
            TestScenario.Context(session, intent: null, realism: RealismProfile.Relaxed));

        Assert.DoesNotContain(decision.Warnings, warning => warning.Code == HoldShortViolation);
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>Issues an IFR clearance and applies it, leaving the readback outstanding.</summary>
    private AtcSession IssueClearance(AtcSession session, SquawkCode? squawk = null)
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request ifr clearance"),
            preassignedSquawk: squawk));

        session = session.WithClearance(Assert.IsType<Clearance>(decision.IssuedClearance));

        return decision.RequiredReadback is PendingReadback readback
            ? session.WithPendingReadback(readback)
            : session;
    }

    /// <summary>A session with the IFR clearance issued and correctly read back.</summary>
    private AtcSession AcknowledgedClearance() =>
        IssueClearance(TestScenario.NewSession(), SquawkCode.Create("4271"))
            .WithReadbackAccepted(TestScenario.Now);

    /// <summary>
    /// A session taxiing out under a taxi clearance that carries a hold-short instruction.
    /// </summary>
    /// <remarks>
    /// The hold-short restriction is what makes runway entry a violation. Without a taxi
    /// clearance carrying one there is nothing to violate, so this helper issues a real one
    /// through the engine rather than fabricating the session state.
    /// </remarks>
    private AtcSession TaxiingWithHoldShort(GeoPosition position)
    {
        AtcSession session = AcknowledgedClearance() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
            Phase = FlightPhase.Pushback,
        };

        AtcDecision taxi = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi")));

        return session
            .WithClearance(Assert.IsType<Clearance>(taxi.IssuedClearance))
            .WithReadbackAccepted(TestScenario.Now)
            with
        {
            Phase = FlightPhase.TaxiOut,
            AircraftState = TestScenario.AtStand() with
            {
                Position = position,
                GroundSpeedKnots = 12,
                ParkingBrakeSet = false,
                EnginesRunning = true,
            },
        };
    }

    /// <summary>A session holding short of runway 18, with tower selected.</summary>
    private AtcSession ReadyForDeparture() => AcknowledgedClearance() with
    {
        Phase = FlightPhase.HoldingShort,
        CurrentController = ControllerPosition.Tower,
        CurrentFrequency = TestScenario.Tower,
        AircraftState = TestScenario.AtHoldingPoint(),
    };

    /// <summary>A session in the climb, working departure.</summary>
    private AtcSession EnRoute() => AcknowledgedClearance() with
    {
        Phase = FlightPhase.InitialClimb,
        CurrentController = ControllerPosition.Departure,
        CurrentFrequency = TestScenario.Departure,
        AircraftState = TestScenario.Airborne(7_000),
        ClearedAltitude = Altitude.FromFeet(7_000),
    };

    /// <summary>A session inbound to STMD, working approach.</summary>
    private AtcSession OnApproach() => AcknowledgedClearance() with
    {
        Phase = FlightPhase.Approach,
        CurrentController = ControllerPosition.Approach,
        CurrentFrequency = TestScenario.Approach,
        AircraftState = TestScenario.Airborne(3_000) with
        {
            Position = GeoPosition.Create(52.85000, 5.10000),
            VerticalSpeedFeetPerMinute = -700,
        },
    };
}
