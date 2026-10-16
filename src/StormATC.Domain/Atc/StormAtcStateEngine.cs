using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

/// <summary>
/// The deterministic ATC state engine. Decides what is permitted, given what is true.
/// </summary>
/// <remarks>
/// <para>
/// Every rule in this class is a pure function of <see cref="AtcEvaluationContext"/>. There is
/// no clock, no network, no persistence, and no randomness. Two identical contexts always
/// produce identical decisions, which is what lets the whole clearance model be tested.
/// </para>
/// <para>
/// The engine is organised as a guard chain followed by an intent dispatch. Guards handle the
/// concerns that apply to every transmission — emergency priority, callsign, confidence,
/// jurisdiction, outstanding readbacks — and each intent handler then deals only with its own
/// preconditions.
/// </para>
/// </remarks>
public sealed partial class StormAtcStateEngine(IReadbackEvaluator readbackEvaluator) : IAtcStateEngine
{
    private readonly IReadbackEvaluator _readbackEvaluator =
        readbackEvaluator ?? throw new ArgumentNullException(nameof(readbackEvaluator));

    /// <summary>
    /// Lateral distance from a runway centreline, in metres, inside which an aircraft is
    /// considered to have entered the protected runway surface.
    /// </summary>
    private const double RunwayIncursionThresholdMeters = 40;

    /// <summary>
    /// Ground speed above which the engine treats the aircraft as committed to the takeoff roll
    /// rather than taxiing.
    /// </summary>
    private const double TakeoffRollGroundSpeedKnots = 45;

    /// <summary>Distance within which an aircraft counts as "at" a stand or holding point.</summary>
    private const double ProximityNauticalMiles = 0.08;

    /// <inheritdoc />
    public AtcDecision Evaluate(AtcEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Intent is not PilotIntent intent)
        {
            return AtcDecision.Silent();
        }

        // --- Guard 1: emergencies bypass every other rule. ---------------------------------
        if (intent.IsEmergency)
        {
            return HandleEmergency(context, intent);
        }

        // --- Guard 2: transmissions that never depend on state. ----------------------------
        switch (intent.Type)
        {
            case PilotIntentType.SayAgain:
                return HandleSayAgain(context);
            case PilotIntentType.RadioCheck:
                return HandleRadioCheck(context);
            case PilotIntentType.RequestFrequency:
                return HandleFrequencyRequest(context);
            case PilotIntentType.Unknown:
                return HandleUnrecognized(context, intent);
        }

        // --- Guard 3: the parser was not sure enough to act on something safety-critical. ---
        if (intent.RequiresClarification
            || (intent.IsSafetyCritical && intent.Confidence < context.Realism.MinimumIntentConfidence))
        {
            return AtcDecision.Reject(
                $"Intent confidence {intent.Confidence:F2} is below the {context.Realism.MinimumIntentConfidence:F2} threshold for a safety-critical request.",
                Say(context).SayAgain(context.Session.Callsign));
        }

        // --- Guard 4: is this transmission from our aircraft? -------------------------------
        if (context.Realism.RequireCorrectCallsign
            && intent.Callsign is Callsign spoken
            && context.Session.Callsign is Callsign expected
            && spoken != expected)
        {
            return AtcDecision.Reject(
                $"Transmission used callsign {spoken} but the session aircraft is {expected}.",
                Say(context).SayAgain(expected));
        }

        // --- Guard 5: is the pilot talking to the right controller? -------------------------
        AtcDecision? jurisdictionFailure = CheckJurisdiction(context, intent);
        if (jurisdictionFailure is not null)
        {
            return jurisdictionFailure;
        }

        // A plain "roger" settles the outstanding readback once the pilot has already tried and
        // failed to read it back, when the profile allows it: a readback nobody can reproduce
        // must not stall the flight. The first "roger" is still sent back for a real readback.
        if (intent.Type == PilotIntentType.PilotAcknowledgement
            && context.Realism.AcceptAcknowledgementAsReadback
            && context.Session.PendingReadback is { IsMandatory: true, AttemptCount: > 0 } outstanding)
        {
            return BuildAcceptedReadbackDecision(
                context,
                outstanding,
                new ReadbackAssessment
                {
                    Outcome = ReadbackOutcome.Correct,
                    CallsignStatus = CallsignStatus.Correct,
                    Confidence = intent.Confidence,
                });
        }

        // --- Guard 6: an outstanding mandatory readback blocks new requests. ----------------
        // Readback and correction intents are exempt: they are how the pilot resolves it.
        bool isReadbackIntent = IsReadbackIntent(intent.Type);
        if (!isReadbackIntent
            && context.Realism.RequireReadbackBeforeProgressing
            && context.Session.HasOutstandingMandatoryReadback)
        {
            return AtcDecision.Reject(
                "A mandatory readback is still outstanding.",
                BuildReadbackReminder(context));
        }

        return intent.Type switch
        {
            PilotIntentType.InitialCall => HandleInitialCall(context),
            PilotIntentType.RequestIFRClearance => HandleClearanceRequest(context),
            PilotIntentType.ClearanceReadback
                or PilotIntentType.TaxiReadback
                or PilotIntentType.TakeoffReadback
                or PilotIntentType.LandingReadback
                or PilotIntentType.AltitudeReadback
                or PilotIntentType.HeadingReadback
                or PilotIntentType.FrequencyReadback
                or PilotIntentType.Correction => HandleReadback(context, intent),
            PilotIntentType.RequestPushback => HandlePushbackRequest(context, intent),
            PilotIntentType.RequestTaxi => DeclareRules(HandleTaxiRequest(context), intent),
            PilotIntentType.VfrArrivalCall => HandleVfrArrivalCall(context, intent),
            PilotIntentType.ReportAtPoint => HandleReportAtPoint(context, intent),
            PilotIntentType.ReportDownwind => HandleCircuitLegReport(context, intent, CircuitLeg.Downwind),
            PilotIntentType.ReportBase => HandleCircuitLegReport(context, intent, CircuitLeg.Base),
            PilotIntentType.ReportFinal => HandleFinalReport(context, intent),
            PilotIntentType.RequestTouchAndGo => HandleTouchAndGoRequest(context, intent),
            PilotIntentType.LeaveFrequency => HandleLeaveFrequency(context),
            PilotIntentType.RequestProgressiveTaxi => HandleProgressiveTaxiRequest(context),
            PilotIntentType.ReportHoldingShort => HandleHoldingShortReport(context, intent),
            PilotIntentType.ReadyForDeparture => HandleReadyForDeparture(context),
            PilotIntentType.ReportAirborne => HandleAirborneReport(context),
            PilotIntentType.CheckIn => HandleCheckIn(context),
            PilotIntentType.RequestClimb => HandleClimbRequest(context, intent),
            PilotIntentType.RequestDescent => HandleDescentRequest(context, intent),
            PilotIntentType.RequestDirect => HandleDirectRequest(context, intent),
            PilotIntentType.RequestDeviation => HandleDeviationRequest(context),
            PilotIntentType.RequestApproach => HandleApproachRequest(context, intent),
            PilotIntentType.ReportEstablished => HandleEstablishedReport(context, intent),
            PilotIntentType.ReportRunwayVacated => HandleRunwayVacated(context),
            PilotIntentType.RequestTaxiToStand => HandleTaxiToStandRequest(context, intent),
            PilotIntentType.RequestRunwayCrossing => HandleRunwayCrossingRequest(context, intent),
            PilotIntentType.GoAround => HandleGoAround(context),
            PilotIntentType.Unable => HandleUnable(context),
            PilotIntentType.PilotAcknowledgement => HandlePilotAcknowledgement(),
            _ => HandleUnrecognized(context, intent),
        };
    }

    /// <inheritdoc />
    public AtcDecision EvaluateStateChange(AtcEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AtcSession session = context.Session;
        Aircraft.AircraftState aircraft = context.AircraftState;

        // --- Unauthorised runway entry. ----------------------------------------------------
        // Checked first because it is the only state change that raises a warning rather than
        // simply advancing the flight.
        AtcDecision? incursion = CheckRunwayIncursion(context);
        if (incursion is not null)
        {
            return incursion;
        }

        // --- Becoming airborne. ------------------------------------------------------------
        if (!aircraft.IsOnGround
            && session.Phase is FlightPhase.Takeoff or FlightPhase.LineUp or FlightPhase.HoldingShort)
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.None,
                NewPhase = FlightPhase.InitialClimb,
                FallbackPhraseology = string.Empty,
                AllowLanguageModelPhraseology = false,
            };
        }

        // --- Departure handoff trigger. ----------------------------------------------------
        // Real towers hand off shortly after the aircraft is established in the climb. The mock
        // trigger is a stable climb through 1000 ft above aerodrome elevation.
        // A VFR aircraft stays with Tower: it flies the circuit or leaves the frequency itself.
        if (session.Phase == FlightPhase.InitialClimb
            && session.FlightRules == FlightRules.Ifr
            && session.CurrentController == ControllerPosition.Tower
            && aircraft.IsClimbing
            && IsAboveAerodromeBy(context, 1000))
        {
            Frequency? departureFrequency = ResolveFrequency(context, ControllerPosition.Departure);
            if (departureFrequency is Frequency frequency && session.Callsign is Callsign callsign)
            {
                return BuildHandoff(
                    context,
                    ControllerPosition.Departure,
                    frequency,
                    FlightPhase.Departure,
                    "Established in the climb after departure.",
                    Say(context).Handoff(callsign, ControllerPosition.Departure, frequency));
            }
        }

        // --- Arrival handoff trigger. -------------------------------------------------------
        // The mirror of the departure handoff: a radar controller passes an arriving aircraft
        // to Approach once it is descending inside the terminal area. Without this the aircraft
        // would still be with Departure or Center when it asks for the approach, and Approach
        // clearance is not theirs to give.
        if (session.Phase == FlightPhase.Descent
            && session.CurrentController is ControllerPosition.Departure or ControllerPosition.Center
            && !aircraft.IsOnGround
            && IsWithinTerminalArea(context))
        {
            Frequency? approachFrequency = ResolveFrequency(context, ControllerPosition.Approach);
            if (approachFrequency is Frequency frequency && session.Callsign is Callsign arriving)
            {
                return BuildHandoff(
                    context,
                    ControllerPosition.Approach,
                    frequency,
                    newPhase: null,
                    "Descending into the terminal area.",
                    Say(context).Handoff(arriving, ControllerPosition.Approach, frequency));
            }
        }

        // --- Go-around handoff. -------------------------------------------------------------
        // Tower gives the go-around instruction; once the aircraft is climbing away it is passed
        // to Approach, which sequences the next approach. A VFR aircraft stays with Tower and
        // rejoins the circuit.
        if (session.Phase == FlightPhase.GoAround
            && session.FlightRules == FlightRules.Ifr
            && session.CurrentController == ControllerPosition.Tower
            && !aircraft.IsOnGround
            && aircraft.IsClimbing
            && IsAboveAirportBy(context.DestinationAirport ?? context.DepartureAirport, aircraft, 500))
        {
            Frequency? approachFrequency = ResolveFrequency(context, ControllerPosition.Approach);
            if (approachFrequency is Frequency frequency && session.Callsign is Callsign callsign)
            {
                return BuildHandoff(
                    context,
                    ControllerPosition.Approach,
                    frequency,
                    FlightPhase.Approach,
                    "Climbing away after a go-around.",
                    Say(context).Handoff(callsign, ControllerPosition.Approach, frequency));
            }
        }

        // --- Departure to Center. -------------------------------------------------------------
        // Departure hands an aircraft that has left the terminal area to the en-route controller.
        // The mirror of the arrival handoff: it follows the aircraft's position, not a timer.
        if (session.Phase == FlightPhase.Departure
            && session.FlightRules == FlightRules.Ifr
            && session.CurrentController == ControllerPosition.Departure
            && !aircraft.IsOnGround
            && context.DepartureAirport is AirportData origin
            && !aircraft.Position.IsWithin(origin.ReferencePosition, TerminalAreaRadiusNauticalMiles))
        {
            Frequency? centerFrequency = ResolveFrequency(context, ControllerPosition.Center);
            if (centerFrequency is Frequency frequency
                && frequency != session.CurrentFrequency
                && session.Callsign is Callsign cruising)
            {
                return BuildHandoff(
                    context,
                    ControllerPosition.Center,
                    frequency,
                    FlightPhase.Enroute,
                    "Leaving the terminal area after departure.",
                    Say(context).Handoff(cruising, ControllerPosition.Center, frequency));
            }
        }

        // --- Touch and go: the wheels touch and the aircraft rolls on. ------------------------
        // No vacate instruction, no handoff: the aircraft is departing again, and once airborne
        // the "becoming airborne" rule above takes it back to the climb.
        if (aircraft.IsOnGround
            && session.Phase is FlightPhase.Final
            && session.ArrivalClearance is { TouchAndGo: true })
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.None,
                NewPhase = FlightPhase.Takeoff,
                NewCircuitLeg = CircuitLeg.None,
                FallbackPhraseology = string.Empty,
                AllowLanguageModelPhraseology = false,
            };
        }

        // --- Touchdown. --------------------------------------------------------------------
        if (aircraft.IsOnGround
            && session.Phase is FlightPhase.Final or FlightPhase.Approach)
        {
            string vacate = BuildVacateInstruction(context);
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.VacateInstruction,
                NewPhase = FlightPhase.Landed,
                FallbackPhraseology = vacate,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = "vacate runway",
                },
                ProhibitedFacts = ["takeoff clearance", "taxi to stand"],
            };
        }

        // --- Parked. -----------------------------------------------------------------------
        if (session.Phase == FlightPhase.TaxiIn
            && aircraft.IsStationary
            && aircraft.ParkingBrakeSet
            && IsAtAssignedStand(context))
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.None,
                NewPhase = FlightPhase.Parked,
                FallbackPhraseology = string.Empty,
                AllowLanguageModelPhraseology = false,
            };
        }

        return AtcDecision.Silent();
    }

    // =====================================================================================
    // Intent handlers
    // =====================================================================================

    private static AtcDecision HandleInitialCall(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Reject(
                "No flight plan is loaded, so the aircraft has no callsign.",
                Say(context).NoFlightPlanToBegin);
        }

        ControllerPosition controller = context.Session.CurrentController == ControllerPosition.None
            ? ControllerPosition.ClearanceDelivery
            : context.Session.CurrentController;

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            FallbackPhraseology = Say(context).GoAhead(callsign, controller),
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["controller"] = Say(context).ControllerName(controller),
            },
            ProhibitedFacts = ["any clearance"],
        };
    }

    private static AtcDecision HandleRadioCheck(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        ControllerPosition controller = context.Session.CurrentController == ControllerPosition.None
            ? ControllerPosition.ClearanceDelivery
            : context.Session.CurrentController;

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.RadioCheckReply,
            FallbackPhraseology = Say(context).RadioCheckReply(callsign, controller),
            ProhibitedFacts = ["any clearance"],
        };
    }

    private static AtcDecision HandleSayAgain(AtcEvaluationContext context)
    {
        // Repeat the last controller transmission verbatim. This is deterministic by design:
        // "say again" means say the same thing, not paraphrase it.
        string? lastControllerText = null;
        IReadOnlyList<ConversationTurn> conversation = context.Session.Conversation;
        for (int i = conversation.Count - 1; i >= 0; i--)
        {
            if (conversation[i].Speaker == ConversationSpeaker.Controller)
            {
                lastControllerText = conversation[i].Text;
                break;
            }
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.SayAgain,
            FallbackPhraseology = lastControllerText
                ?? Say(context).SayAgain(context.Session.Callsign),
            AllowLanguageModelPhraseology = false,
        };
    }

    private static AtcDecision HandleUnrecognized(AtcEvaluationContext context, PilotIntent intent)
    {
        string reason = intent.ClarificationReason ?? "The transmission was not understood.";

        return new AtcDecision
        {
            Accepted = false,
            ResponseType = AtcResponseType.SayAgain,
            RejectionReason = reason,
            FallbackPhraseology = Say(context).SayAgain(context.Session.Callsign),
            AllowLanguageModelPhraseology = false,
        };
    }

    private static AtcDecision HandleUnable(AtcEvaluationContext context)
    {
        // The pilot has declined an instruction. Acknowledge and leave the clearance in place;
        // resolving it is the pilot's next transmission, not something to guess at here.
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            FallbackPhraseology = Say(context).RogerSayIntentions(callsign),
            ProhibitedFacts = ["new clearance"],
        };
    }

    private static AtcDecision HandleEmergency(AtcEvaluationContext context, PilotIntent intent)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        bool isMayday = intent.Type == PilotIntentType.Mayday;

        var warning = new AtcWarning
        {
            Severity = AtcSeverity.Warning,
            Code = isMayday ? "MaydayDeclared" : "PanPanDeclared",
            Message = isMayday
                ? "Simulated emergency declared (MAYDAY). This is a flight simulation only."
                : "Simulated urgency declared (PAN PAN). This is a flight simulation only.",
            RaisedAt = context.CurrentTime,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.EmergencyAcknowledgement,
            NewPhase = FlightPhase.Abnormal,
            FallbackPhraseology = Say(context).EmergencyAcknowledgement(callsign, isMayday),
            Warnings = [warning],
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = isMayday ? "mayday acknowledged" : "pan pan acknowledged",
                ["request"] = "say intentions",
            },

            // The model must not offer assistance StormATC cannot simulate, nor imply that any
            // real emergency service is involved.
            ProhibitedFacts =
            [
                "emergency services dispatched",
                "real-world assistance",
                "vectors to nearest airport",
                "any clearance not listed in approved facts",
            ],
        };
    }

    private static AtcDecision HandleClearanceRequest(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.FlightPlan is not FlightPlanning.FlightPlan plan || session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Reject(
                "No flight plan is loaded.",
                Say(context).NoFlightPlanFirst);
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "IFR clearance may only be requested on the ground before departure.",
                Say(context).Unable(callsign, UnableReason.AlreadyAirborne));
        }

        if (session.Phase is not (FlightPhase.Preflight or FlightPhase.ClearanceRequested or FlightPhase.Disconnected))
        {
            return AtcDecision.Reject(
                $"Clearance cannot be issued in the {session.Phase} phase.",
                Say(context).Unable(callsign, UnableReason.ClearanceAlreadyIssued));
        }

        // A SimBrief plan can name the runway even when StormATC has no detailed ground-layout
        // file for that real aerodrome. That is enough to issue an en-route IFR clearance: omit
        // local frequencies and taxiways rather than rejecting the entire clearance.
        RunwayIdentifier? runway = plan.DepartureRunway
            ?? context.DepartureAirport?
                .SelectActiveRunway(context.Weather?.WindDirection)?.Identifier;

        if (runway is not RunwayIdentifier departureRunway)
        {
            return AtcDecision.Reject(
                context.DepartureAirport is null
                    ? "No airport data or filed departure runway is available."
                    : "The departure aerodrome publishes no usable runway.",
                Say(context).Unable(callsign, UnableReason.NoDepartureRunwayAvailable));
        }

        Altitude initialAltitude = ResolveInitialAltitude(context, plan);
        SquawkCode squawk = ResolveSquawk(context, callsign);
        Frequency? departureFrequency = ResolveFrequency(context, ControllerPosition.Departure);
        bool alsoRequestedPushback =
            context.Transcript.Contains("pushback", StringComparison.OrdinalIgnoreCase)
            || context.Transcript.Contains("request push", StringComparison.OrdinalIgnoreCase);

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.IfrClearance,
            IssuedBy = ControllerPosition.ClearanceDelivery,
            IssuedAt = context.CurrentTime,
            ClearedToDestination = plan.DestinationIcao,
            ClearedRoute = plan.Route ?? "flight planned route",
            InitialAltitude = initialAltitude,
            CruiseAltitude = plan.CruiseAltitude,
            Runway = departureRunway,
            DepartureProcedure = plan.StandardInstrumentDeparture,
            Squawk = squawk,
            NextFrequency = departureFrequency,
            RequiredReadbackItems = BuildClearanceReadbackItemTypes(departureFrequency),
        };

        var expectedItems = new List<ExpectedReadbackItem>
        {
            new()
            {
                Type = ReadbackItemType.Callsign,
                ExpectedValue = callsign.Normalized,
                SpokenValue = Say(context).SpeakCallsign(callsign),
                IsCritical = true,
            },
            new()
            {
                Type = ReadbackItemType.Runway,
                ExpectedValue = departureRunway.ToComparisonValue(),
                SpokenValue = Say(context).SpeakRunway(departureRunway),
                IsCritical = true,
            },
            new()
            {
                Type = ReadbackItemType.InitialAltitude,
                ExpectedValue = initialAltitude.ToComparisonValue(),
                SpokenValue = Say(context).ReadbackItemInitialAltitude(initialAltitude),
                IsCritical = true,
            },
            new()
            {
                Type = ReadbackItemType.Squawk,
                ExpectedValue = squawk.ToComparisonValue(),
                SpokenValue = Say(context).ReadbackItemSquawk(squawk),
                IsCritical = true,
            },
        };

        if (departureFrequency is Frequency frequency)
        {
            expectedItems.Add(new ExpectedReadbackItem
            {
                Type = ReadbackItemType.Frequency,
                ExpectedValue = frequency.ToComparisonValue(),
                SpokenValue = Say(context).ReadbackItemDepartureFrequency(frequency),

                // The departure frequency is read back in practice but a missing readback is a
                // phraseology slip, not a safety issue — the pilot is told again at handoff.
                IsCritical = false,
            });
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.IfrClearance,
            NewPhase = FlightPhase.IFRClearanceIssued,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.IfrClearance,
                ExpectedItems = expectedItems,
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = clearance.ToRequiredFacts(),
            ProhibitedFacts =
            [
                "taxi clearance",
                "pushback approval",
                "takeoff clearance",
                "any altitude other than the approved initial altitude",
            ],
            FallbackPhraseology = alsoRequestedPushback
                ? Say(context).IfrClearance(callsign, clearance)
                    + Say(context).ClearanceThenPushbackNote
                : Say(context).IfrClearance(callsign, clearance),

            // A compound clearance-and-pushback request needs the explicit sequencing sentence
            // above. Do not let a style model omit it and silently ignore half the request.
            AllowLanguageModelPhraseology = !alsoRequestedPushback,
        };
    }

    private AtcDecision HandleReadback(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.PendingReadback is not PendingReadback pending)
        {
            // Nothing outstanding. Acknowledge rather than refuse — a pilot reading back an
            // instruction the engine considers already settled is harmless.
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                FallbackPhraseology = session.Callsign is Callsign cs
                    ? Say(context).Acknowledgement(cs)
                    : Say(context).RogerPlain,
                ProhibitedFacts = ["any new clearance"],
            };
        }

        ReadbackAssessment assessment = _readbackEvaluator.Evaluate(
            pending, intent, context.Transcript, session, context.Realism);

        if (!assessment.IsAccepted)
        {
            IReadOnlyList<ReadbackItemType> satisfiedItems =
                assessment.Outcome == ReadbackOutcome.WrongCallsign
                    ? pending.SatisfiedItems
                    : assessment.CorrectItems;

            return new AtcDecision
            {
                Accepted = false,
                ResponseType = AtcResponseType.ClearanceReadbackCorrection,
                RejectionReason = $"Readback {assessment.Outcome}.",
                ReadbackAssessment = assessment,
                RequiredReadback = pending.WithAttempt(satisfiedItems),
                FallbackPhraseology = assessment.RequiredCorrection
                    ?? Say(context).SayAgain(session.Callsign),

                // Corrections are spoken exactly as generated. Letting a model rephrase a
                // correction risks it restating the wrong value the pilot just used.
                AllowLanguageModelPhraseology = false,
            };
        }

        return BuildAcceptedReadbackDecision(context, pending, assessment);
    }

    /// <summary>
    /// Builds the response to a correct readback, including the handoff that usually follows.
    /// </summary>
    private static AtcDecision BuildAcceptedReadbackDecision(
        AtcEvaluationContext context,
        PendingReadback pending,
        ReadbackAssessment assessment)
    {
        AtcSession session = context.Session;
        Callsign? callsign = session.Callsign;

        // After a correct IFR clearance readback the aircraft moves to Ground.
        if (pending.ClearanceType == ClearanceType.IfrClearance && callsign is Callsign cs)
        {
            Frequency? groundFrequency = ResolveFrequency(context, ControllerPosition.Ground);

            if (groundFrequency is Frequency ground)
            {
                var handoff = new ControllerHandoff
                {
                    FromController = ControllerPosition.ClearanceDelivery,
                    ToController = ControllerPosition.Ground,
                    ToFrequency = ground,
                    InstructedAt = context.CurrentTime,
                    State = HandoffState.Instructed,
                    Reason = "IFR clearance readback accepted.",
                };

                return new AtcDecision
                {
                    Accepted = true,
                    ResponseType = AtcResponseType.Handoff,
                    ClearsPendingReadback = true,
                    Handoff = handoff,
                    ReadbackAssessment = assessment,
                    ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["readback"] = "correct",
                        ["nextController"] = Say(context).ControllerName(ControllerPosition.Ground),
                        ["frequency"] = ground.ToComparisonValue(),
                    },
                    ProhibitedFacts = ["taxi clearance", "pushback approval"],
                    FallbackPhraseology = Say(context).ReadbackCorrect(
                        cs, ground, ControllerPosition.Ground),
                };
            }
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            ClearsPendingReadback = true,
            ReadbackAssessment = assessment,
            NewPhase = ResolvePhaseAfterReadback(session, pending),
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["readback"] = "correct",
            },
            ProhibitedFacts = ["any new clearance"],
            FallbackPhraseology = Say(context).ReadbackCorrectPlain(callsign),
        };
    }

    /// <summary>
    /// Some readbacks imply a phase change: acknowledging a taxi clearance means the aircraft
    /// is taxiing, acknowledging a takeoff clearance means it is departing.
    /// </summary>
    private static FlightPhase? ResolvePhaseAfterReadback(AtcSession session, PendingReadback pending) =>
        pending.ClearanceType switch
        {
            ClearanceType.Taxi when session.Phase is FlightPhase.Pushback or FlightPhase.IFRClearanceIssued
                => FlightPhase.TaxiOut,
            ClearanceType.TaxiToStand when session.Phase == FlightPhase.Landed
                => FlightPhase.TaxiIn,
            ClearanceType.LineUp => FlightPhase.LineUp,
            ClearanceType.Takeoff => FlightPhase.Takeoff,
            ClearanceType.LandingClearance => FlightPhase.Final,
            _ => null,
        };

    private static AtcDecision HandlePushbackRequest(
        AtcEvaluationContext context,
        PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "Pushback cannot be approved for an airborne aircraft.",
                Say(context).Unable(callsign, UnableReason.Airborne));
        }

        // Moving faster than a walking pace means the aircraft is already taxiing.
        if (context.AircraftState.GroundSpeedKnots > 3)
        {
            return AtcDecision.Reject(
                "The aircraft is already moving; pushback is not applicable.",
                Say(context).Unable(callsign, UnableReason.AlreadyMoving));
        }

        if (session.Phase is not (FlightPhase.Preflight or FlightPhase.IFRClearanceIssued or FlightPhase.ClearanceRequested))
        {
            return AtcDecision.Reject(
                $"Pushback is not available in the {session.Phase} phase.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        // Realism setting decides whether an unread-back clearance blocks pushback. Guard 6
        // has already handled the mandatory case; this covers the relaxed profile explicitly.
        if (context.Realism.RequireReadbackBeforeProgressing
            && session.IfrClearance is { IsAcknowledged: false })
        {
            return AtcDecision.Reject(
                "The IFR clearance has not been read back.",
                Say(context).Unable(callsign, UnableReason.ReadBackClearanceFirst));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Pushback,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = context.CurrentTime,
        };

        // A pilot-requested direction wins. The configured relative default is used otherwise
        // because aircraft heading alone cannot reveal which apron lead-out is appropriate.
        string pushbackDirection =
            PushbackDirections.Normalize(intent.Entities.PushbackDirection)
            ?? PushbackDirections.Normalize(context.DefaultPushbackDirection)
            ?? PushbackDirections.TailLeft;
        var approvedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approval"] = "pushback approved",
            ["pushbackDirection"] = pushbackDirection,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.PushbackApproval,
            NewPhase = FlightPhase.Pushback,
            IssuedClearance = clearance,
            ApprovedFacts = approvedFacts,
            ProhibitedFacts = ["taxi clearance", "takeoff clearance", "runway assignment"],
            FallbackPhraseology =
                Say(context).PushbackApproval(callsign, pushbackDirection),
        };
    }

    private static AtcDecision HandleTaxiRequest(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "Taxi cannot be issued to an airborne aircraft.",
                Say(context).Unable(callsign, UnableReason.Airborne));
        }

        if (session.Phase is not (FlightPhase.Preflight or FlightPhase.IFRClearanceIssued
            or FlightPhase.Pushback or FlightPhase.TaxiOut))
        {
            return AtcDecision.Reject(
                $"Taxi is not available in the {session.Phase} phase.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        RunwayIdentifier? runway = session.AssignedDepartureRunway
            ?? session.IfrClearance?.Runway
            ?? session.FlightPlan?.DepartureRunway
            ?? context.DepartureAirport?
                .SelectActiveRunway(context.Weather?.WindDirection)?.Identifier;

        if (runway is not RunwayIdentifier departureRunway)
        {
            return AtcDecision.Reject(
                "No departure runway has been assigned.",
                Say(context).Unable(callsign, UnableReason.NoDepartureRunwayAssigned));
        }

        // Most imported SimBrief aerodromes do not yet have a bundled StormATC taxi graph. The
        // filed runway is still enough to issue a limited clearance to its holding point. Never
        // invent taxiway names: tell the pilot that detailed routing is unavailable and require
        // the runway hold-short readback.
        if (context.DepartureAirport is not AirportData airport)
        {
            return BuildUnroutedTaxiClearance(context, callsign, departureRunway);
        }

        // A detailed route is computed from the aircraft's current simulator position. The
        // assigned stand is only a fallback for sessions created before a live position was
        // available. There is no path here that invents a taxiway name.
        if (!airport.SupportsTaxiRouting)
        {
            return BuildUnroutedTaxiClearance(context, callsign, departureRunway);
        }

        TaxiRoute route = TaxiPathfinder.FindRouteFromPositionToHoldingPoint(
            airport,
            context.AircraftState.Position,
            departureRunway);

        if (!route.Found
            && ExistingStand(airport, session.AssignedStand) is string assignedStand)
        {
            route = TaxiPathfinder.FindRouteToHoldingPoint(
                airport,
                assignedStand,
                departureRunway);
        }

        if (!route.Found)
        {
            return BuildUnroutedTaxiClearance(context, callsign, departureRunway);
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Taxi,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = context.CurrentTime,
            Runway = departureRunway,
            TaxiRoute = route.TaxiwayNames,
            TaxiPathNodes = route.Nodes,
            TaxiPathTaxiways = route.Edges.Select(edge => edge.TaxiwayName).ToArray(),
            TaxiRouteLengthMeters = route.TotalLengthMeters,
            TaxiDestination = $"holding point {departureRunway.ToDisplay()}",
            HoldShortOfRunway = departureRunway,
            RequiredReadbackItems = route.TaxiwayNames.Count > 0
                ? [ReadbackItemType.TaxiRoute, ReadbackItemType.HoldShortRunway]
                : [ReadbackItemType.HoldShortRunway],
        };

        var expectedItems = new List<ExpectedReadbackItem>();

        // The route is read back only when it names taxiways. Aerodromes whose taxiways are
        // unnamed in the simulator data give a route with no names, and "via" followed by
        // nothing is a readback nobody could ever complete.
        if (route.TaxiwayNames.Count > 0)
        {
            expectedItems.Add(new()
            {
                Type = ReadbackItemType.TaxiRoute,
                ExpectedValue = string.Join(",", route.TaxiwayNames),
                SpokenValue = Say(context).ReadbackItemTaxiRoute(route.TaxiwayNames),

                // A misheard taxi route sends the aircraft the wrong way on the manoeuvring
                // area, so it is treated as critical.
                IsCritical = true,
            });
        }

        expectedItems.Add(new()
        {
            Type = ReadbackItemType.HoldShortRunway,
            ExpectedValue = departureRunway.ToComparisonValue(),
            SpokenValue = Say(context).ReadbackItemHoldShort(departureRunway),
            IsCritical = true,
        });

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TaxiClearance,
            NewPhase = FlightPhase.TaxiOut,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.Taxi,
                ExpectedItems = expectedItems,
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = clearance.ToRequiredFacts(),
            ProhibitedFacts =
            [
                "takeoff clearance",
                "line up",
                $"cross runway {departureRunway.ToDisplay()}",
                "any taxiway not listed in the approved taxi route",
            ],
            FallbackPhraseology = Say(context).TaxiClearance(callsign, clearance),
        };
    }

    private static AtcDecision HandlePilotAcknowledgement() => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.None,
        FallbackPhraseology = string.Empty,
        AllowLanguageModelPhraseology = false,
        ProhibitedFacts = ["any clearance"],
    };

    private static AtcDecision HandleProgressiveTaxiRequest(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "Progressive taxi is only available on the ground.",
                Say(context).Unable(callsign, UnableReason.ProgressiveTaxiGroundOnly));
        }

        if (session.TaxiClearance is not Clearance clearance
            || context.RelevantAirport?.TaxiGraph is not TaxiGraph graph
            || clearance.TaxiPathNodes.Count == 0)
        {
            return AtcDecision.Reject(
                "There is no active taxi path to guide.",
                Say(context).Unable(callsign, UnableReason.NoActiveTaxiRoute));
        }

        TaxiRoute route = TaxiPathfinder.FromNodeSequence(
            graph,
            clearance.TaxiPathNodes,
            clearance.TaxiPathTaxiways);
        TaxiRouteProgress progress = TaxiSurfaceAdvisor.Analyze(
            graph,
            route,
            context.AircraftState.Position);

        if (!progress.Found || !progress.IsOnRoute)
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.HoldShortInstruction,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instruction"] = "hold position",
                },
                ProhibitedFacts = ["taxi route guess", "runway crossing"],
                AllowLanguageModelPhraseology = false,
                FallbackPhraseology = Say(context).HoldPositionConfirm(callsign),
            };
        }

        ProgressiveTaxiGuidance guidance;

        if (progress.IsAtDestination || progress.IsApproachingHoldShort)
        {
            guidance = new ProgressiveTaxiGuidance
            {
                Step = clearance.Runway is not null
                    ? ProgressiveTaxiStep.ToHoldingPoint
                    : ProgressiveTaxiStep.ToDestination,
                CurrentTaxiway = progress.CurrentTaxiway,
                Runway = clearance.Runway,
                Destination = clearance.TaxiDestination,
            };
        }
        else if (progress.NextTaxiway is { Length: > 0 } next
            && progress.NextTurn is TaxiTurnDirection turn)
        {
            guidance = new ProgressiveTaxiGuidance
            {
                Step = turn switch
                {
                    TaxiTurnDirection.Left => ProgressiveTaxiStep.TurnLeft,
                    TaxiTurnDirection.Right => ProgressiveTaxiStep.TurnRight,
                    _ => ProgressiveTaxiStep.Straight,
                },
                CurrentTaxiway = progress.CurrentTaxiway,
                NextTaxiway = next,
            };
        }
        else
        {
            guidance = new ProgressiveTaxiGuidance
            {
                Step = ProgressiveTaxiStep.ToDestination,
                CurrentTaxiway = progress.CurrentTaxiway,
                Destination = clearance.TaxiDestination,
            };
        }

        string instruction = Say(context).ProgressiveTaxiInstruction(guidance);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["progressiveTaxi"] = instruction,
            },
            ProhibitedFacts = ["new taxiway", "runway crossing", "takeoff clearance"],
            AllowLanguageModelPhraseology = false,
            FallbackPhraseology = $"{Say(context).SpeakCallsign(callsign)}, {instruction}.",
        };
    }

    private static AtcDecision HandleFrequencyRequest(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        ControllerPosition target = InferFrequencyTarget(context);
        AirportFrequencies? published = context.RelevantAirport?.Frequencies;
        Frequency? exact = published?.For(target);

        if (exact is Frequency frequency)
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["controller"] = Say(context).ControllerName(target),
                    ["frequency"] = frequency.ToComparisonValue(),
                },
                ProhibitedFacts = ["any clearance"],
                FallbackPhraseology = Say(context).FrequencyInformation(callsign, target, frequency),
                AllowLanguageModelPhraseology = false,
            };
        }

        // No separate published frequency means the local position is combined. Keeping the
        // aircraft on its present channel is safer than inventing a real-world frequency.
        if (target is ControllerPosition.ClearanceDelivery
            or ControllerPosition.Ground
            or ControllerPosition.Tower)
        {
            return new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                ProhibitedFacts = ["any frequency not present in airport data", "any clearance"],
                FallbackPhraseology = Say(context).NoSeparateFrequency(callsign, target),
                AllowLanguageModelPhraseology = false,
            };
        }

        Frequency? fallback = ResolveFrequency(context, target);
        return fallback is Frequency resolved
            ? new AtcDecision
            {
                Accepted = true,
                ResponseType = AtcResponseType.Acknowledgement,
                ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["controller"] = Say(context).ControllerName(target),
                    ["frequency"] = resolved.ToComparisonValue(),
                },
                ProhibitedFacts = ["any clearance"],
                FallbackPhraseology = Say(context).FrequencyInformation(callsign, target, resolved),
                AllowLanguageModelPhraseology = false,
            }
            : AtcDecision.Reject(
                $"No frequency data is available for {target}.",
                Say(context).FrequencyUnavailable(callsign, target));
    }

    private static ControllerPosition InferFrequencyTarget(AtcEvaluationContext context)
    {
        string transcript = context.Transcript;
        foreach ((string word, ControllerPosition controller) in new[]
        {
            ("delivery", ControllerPosition.ClearanceDelivery),
            ("ground", ControllerPosition.Ground),
            ("tower", ControllerPosition.Tower),
            ("departure", ControllerPosition.Departure),
            ("approach", ControllerPosition.Approach),
            ("center", ControllerPosition.Center),
            ("centre", ControllerPosition.Center),
        })
        {
            if (transcript.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return controller;
            }
        }

        // A generic "what's the frequency?" usually refers to the controller named in the
        // immediately preceding handoff instruction.
        IReadOnlyList<ConversationTurn> conversation = context.Session.Conversation;
        for (int index = conversation.Count - 1; index >= 0; index--)
        {
            ConversationTurn turn = conversation[index];
            if (turn.Speaker != ConversationSpeaker.Controller)
            {
                continue;
            }

            string text = turn.Text;
            IAtcPhraseology say = Say(context);
            foreach (ControllerPosition position in new[]
            {
                ControllerPosition.Ground,
                ControllerPosition.Tower,
                ControllerPosition.Departure,
                ControllerPosition.Approach,
            })
            {
                if (say.MentionsContact(text, position))
                {
                    return position;
                }
            }

            break;
        }

        AtcSession session = context.Session;
        if (session.CurrentController == ControllerPosition.ClearanceDelivery
            && session.Phase is FlightPhase.IFRClearanceIssued
                or FlightPhase.Pushback
                or FlightPhase.TaxiOut)
        {
            return ControllerPosition.Ground;
        }

        return session.CurrentController == ControllerPosition.None
            ? ControllerPosition.ClearanceDelivery
            : session.CurrentController;
    }

    private static AtcDecision BuildUnroutedTaxiClearance(
        AtcEvaluationContext context,
        Callsign callsign,
        RunwayIdentifier departureRunway)
    {
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Taxi,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = context.CurrentTime,
            Runway = departureRunway,
            TaxiDestination = $"holding point {departureRunway.ToDisplay()}",
            HoldShortOfRunway = departureRunway,
            RequiredReadbackItems = [ReadbackItemType.HoldShortRunway],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TaxiClearance,
            NewPhase = FlightPhase.TaxiOut,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.Taxi,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.HoldShortRunway,
                        ExpectedValue = departureRunway.ToComparisonValue(),
                        SpokenValue = Say(context).ReadbackItemHoldShort(departureRunway),
                        IsCritical = true,
                    },
                ],
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = clearance.ToRequiredFacts(),
            ProhibitedFacts =
            [
                "takeoff clearance",
                "line up",
                $"cross runway {departureRunway.ToDisplay()}",
                "any named taxiway",
            ],
            FallbackPhraseology = Say(context).UnroutedTaxiClearance(callsign, departureRunway),
        };
    }

    private static AtcDecision HandleHoldingShortReport(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        RunwayIdentifier? reported = intent.Entities.Runway ?? intent.Entities.HoldShortRunway;
        RunwayIdentifier? assigned = session.AssignedDepartureRunway;

        // A pilot reporting at the wrong holding point is a real and common error worth
        // catching: it means the aircraft is somewhere the controller does not expect.
        if (reported is RunwayIdentifier reportedRunway
            && assigned is RunwayIdentifier assignedRunway
            && reportedRunway != assignedRunway)
        {
            return AtcDecision.Reject(
                $"Pilot reported holding short of {reportedRunway} but the assigned departure runway is {assignedRunway}.",
                Say(context).WrongHoldingPoint(callsign, assignedRunway));
        }

        Frequency? towerFrequency = ResolveFrequency(context, ControllerPosition.Tower);

        // Ground hands to Tower at the holding point.
        if (session.CurrentController == ControllerPosition.Ground && towerFrequency is Frequency tower)
        {
            return BuildHandoff(
                context,
                ControllerPosition.Tower,
                tower,
                FlightPhase.HoldingShort,
                "Reported holding short of the departure runway.",
                Say(context).Handoff(callsign, ControllerPosition.Tower, tower));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewPhase = FlightPhase.HoldingShort,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = "holding short acknowledged",
            },
            ProhibitedFacts = ["takeoff clearance", "line up clearance"],
            FallbackPhraseology = Say(context).RogerHoldShort(callsign),
        };
    }

    private static AtcDecision HandleReadyForDeparture(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        // --- Takeoff clearance preconditions, checked in the order a controller would. ------

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "The aircraft is already airborne.",
                Say(context).Unable(callsign, UnableReason.AlreadyAirborne));
        }

        if (session.CurrentController != ControllerPosition.Tower)
        {
            return AtcDecision.Reject(
                $"Takeoff clearance requires Tower; the aircraft is with {session.CurrentController}.",
                Say(context).Unable(callsign, UnableReason.ContactTowerForDeparture));
        }

        if (session.Phase is not (FlightPhase.HoldingShort or FlightPhase.LineUp or FlightPhase.TaxiOut))
        {
            return AtcDecision.Reject(
                $"Takeoff clearance is not available in the {session.Phase} phase.",
                Say(context).Unable(callsign, UnableReason.NotAtHoldingPoint));
        }

        if (session.AssignedDepartureRunway is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No departure runway has been assigned.",
                Say(context).Unable(callsign, UnableReason.NoDepartureRunwayAssigned));
        }

        // The mock runway-occupancy model: any traffic reported on this runway blocks it.
        if (IsRunwayOccupied(context, runway))
        {
            return AtcDecision.Reject(
                $"Runway {runway} is occupied.",
                Say(context).HoldShortTrafficOnRunway(callsign, runway));
        }

        // Landing traffic on short final to this runway, or to the opposite end of it, is as
        // much a conflict as traffic already on the surface.
        if (FindTrafficOnShortFinal(context, runway) is not null)
        {
            return AtcDecision.Reject(
                $"Traffic is on short final for runway {runway}.",
                Say(context).HoldPositionTrafficOnShortFinal(callsign, runway));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Takeoff,
            IssuedBy = ControllerPosition.Tower,
            IssuedAt = context.CurrentTime,
            Runway = runway,
            InitialAltitude = session.ClearedAltitude,
            RequiredReadbackItems = [ReadbackItemType.Runway],
        };

        var expectedItems = new List<ExpectedReadbackItem>
        {
            new()
            {
                Type = ReadbackItemType.Runway,
                ExpectedValue = runway.ToComparisonValue(),
                SpokenValue = Say(context).SpeakRunway(runway),
                IsCritical = true,
            },
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TakeoffClearance,
            NewPhase = FlightPhase.Takeoff,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.Takeoff,
                ExpectedItems = expectedItems,
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = BuildTakeoffFacts(context, runway),
            ProhibitedFacts =
            [
                "any runway other than the assigned runway",
                "landing clearance",
                "wind information unless supplied in approved facts",
            ],
            FallbackPhraseology = Say(context).TakeoffClearance(
                callsign,
                runway,
                context.Weather?.WindDirection,
                context.Weather?.WindSpeedKnots,
                initialHeading: null),
        };
    }

    private static AtcDecision HandleAirborneReport(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewPhase = context.AircraftState.IsOnGround ? null : FlightPhase.InitialClimb,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = "roger",
            },
            ProhibitedFacts = ["any new clearance"],
            FallbackPhraseology = Say(context).Acknowledgement(callsign),
        };
    }

    private static AtcDecision HandleCheckIn(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        ControllerPosition controller = session.CurrentController;

        // Departure's job on check-in is to confirm radar contact and clear the aircraft to its
        // cruise level, if one is filed and it is above the current cleared altitude.
        Altitude? climbTo = null;
        Clearance? climbClearance = null;

        if (controller is ControllerPosition.Departure or ControllerPosition.Center
            && session.FlightPlan?.CruiseAltitude is Altitude cruise
            && (session.ClearedAltitude is null || cruise > session.ClearedAltitude.Value))
        {
            climbTo = cruise;
            climbClearance = new Clearance
            {
                ClearanceId = Guid.CreateVersion7(),
                Type = ClearanceType.Climb,
                IssuedBy = controller,
                IssuedAt = context.CurrentTime,
                InitialAltitude = cruise,
                RequiredReadbackItems = [ReadbackItemType.CruiseAltitude],
            };
        }

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["controller"] = Say(context).ControllerName(controller),
            ["radarContact"] = "true",
        };

        if (climbTo is Altitude target)
        {
            facts["climbAltitude"] = target.ToComparisonValue();
        }

        PendingReadback? readback = climbClearance is null ? null : new PendingReadback
        {
            ClearanceId = climbClearance.ClearanceId,
            ClearanceType = ClearanceType.Climb,
            ExpectedItems =
            [
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.CruiseAltitude,
                    ExpectedValue = climbTo!.Value.ToComparisonValue(),
                    SpokenValue = Say(context).ReadbackItemAltitudeChange(climbTo.Value, isClimb: true),
                    IsCritical = true,
                },
            ],
            IssuedAt = context.CurrentTime,
            IsMandatory = true,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.CheckInAcknowledgement,
            NewPhase = controller == ControllerPosition.Center ? FlightPhase.Enroute : null,
            IssuedClearance = climbClearance,
            RequiredReadback = readback,
            ApprovedFacts = facts,
            ProhibitedFacts = ["any altitude other than the approved climb altitude", "traffic information"],
            FallbackPhraseology = Say(context).CheckInAcknowledgement(
                callsign, controller, climbTo, radarContact: true),
        };
    }

    private static AtcDecision HandleClimbRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (session.CurrentController is not (ControllerPosition.Departure or ControllerPosition.Center
            or ControllerPosition.Approach))
        {
            return AtcDecision.Reject(
                "Climb requests are handled by a radar controller.",
                Say(context).Unable(callsign, UnableReason.NotWithThisController));
        }

        // The requested level, or the filed cruise level when the pilot did not name one.
        Altitude? requested = intent.Entities.Altitude ?? session.FlightPlan?.CruiseAltitude;

        if (requested is not Altitude target)
        {
            return AtcDecision.Reject(
                "No climb level was specified and no cruise level is filed.",
                Say(context).SayRequestedLevel(callsign));
        }

        // Never approve a climb above the filed cruise level: the engine has no traffic picture
        // above it and would be inventing airspace clearance.
        if (session.FlightPlan?.CruiseAltitude is Altitude filedCruise && target > filedCruise)
        {
            return AtcDecision.Reject(
                $"Requested level {target} is above the filed cruise level {filedCruise}.",
                Say(context).UnableMaintain(callsign, filedCruise));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Climb,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            InitialAltitude = target,
            RequiredReadbackItems = [ReadbackItemType.CruiseAltitude],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.ClimbInstruction,
            IssuedClearance = clearance,
            RequiredReadback = BuildAltitudeReadback(context, clearance, target, isClimb: true),
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["climbAltitude"] = target.ToComparisonValue(),
            },
            ProhibitedFacts = ["any altitude other than the approved climb altitude"],
            FallbackPhraseology = Say(context).ClimbInstruction(callsign, target),
        };
    }

    private static AtcDecision HandleDescentRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (session.CurrentController is not (ControllerPosition.Center or ControllerPosition.Approach
            or ControllerPosition.Departure))
        {
            return AtcDecision.Reject(
                "Descent requests are handled by a radar controller.",
                Say(context).Unable(callsign, UnableReason.NotWithThisController));
        }

        // Default to a plausible initial descent level when the pilot simply requests descent.
        Altitude target = intent.Entities.Altitude ?? Altitude.FromFlightLevel(70);

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Descent,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            InitialAltitude = target,
            RequiredReadbackItems = [ReadbackItemType.CruiseAltitude],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.DescentInstruction,
            NewPhase = FlightPhase.Descent,
            IssuedClearance = clearance,
            RequiredReadback = BuildAltitudeReadback(context, clearance, target, isClimb: false),
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["descentAltitude"] = target.ToComparisonValue(),
            },
            ProhibitedFacts = ["any altitude other than the approved descent altitude", "approach clearance"],
            FallbackPhraseology = Say(context).DescentInstruction(callsign, target),
        };
    }

    private static AtcDecision HandleDirectRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (intent.Entities.Waypoint is not { Length: > 0 } waypoint)
        {
            return AtcDecision.Reject(
                "No waypoint was identified in the direct-to request.",
                Say(context).SayAgainWaypoint(callsign));
        }

        // The waypoint must exist on the filed route. Clearing an aircraft direct to a fix the
        // engine has never heard of would be inventing navigation data.
        bool onRoute = session.FlightPlan?.RouteLegs
            .Any(leg => string.Equals(leg.Identifier, waypoint, StringComparison.OrdinalIgnoreCase)) ?? false;

        if (!onRoute)
        {
            return AtcDecision.Reject(
                $"Waypoint {waypoint} is not on the filed route.",
                Say(context).Unable(callsign, UnableReason.WaypointNotOnRoute));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.DirectTo,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            ClearedRoute = $"DCT {waypoint}",
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.DirectToApproval,
            IssuedClearance = clearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["directTo"] = waypoint,
            },
            ProhibitedFacts = ["any waypoint other than the approved one", "altitude change"],
            FallbackPhraseology = Say(context).DirectToApproval(callsign, waypoint),
        };
    }

    private static AtcDecision HandleDeviationRequest(AtcEvaluationContext context)
    {
        if (context.Session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        // Weather deviations are approved only in terms the engine can express. With no weather
        // provider there is nothing to quote, so the approval stays generic.
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Heading,
            IssuedBy = context.Session.CurrentController,
            IssuedAt = context.CurrentTime,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            IssuedClearance = clearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["approval"] = "deviation approved",
                ["condition"] = "advise when able to resume own navigation",
            },
            ProhibitedFacts = ["specific weather information", "traffic information", "altitude change"],
            FallbackPhraseology = Say(context).DeviationApproved(callsign),
        };
    }

    private static AtcDecision HandleApproachRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (session.CurrentController is not (ControllerPosition.Approach or ControllerPosition.Center))
        {
            return AtcDecision.Reject(
                "Approach clearance requires Approach control.",
                Say(context).Unable(callsign, UnableReason.ContactApproach));
        }

        if (context.DestinationAirport is not AirportData airport)
        {
            return AtcDecision.Reject(
                "No airport data is available for the destination.",
                Say(context).Unable(callsign, UnableReason.DestinationDataUnavailable));
        }

        RunwayIdentifier? runway = intent.Entities.Runway
            ?? session.FlightPlan?.ArrivalRunway
            ?? airport.SelectActiveRunway(context.Weather?.WindDirection)?.Identifier;

        if (runway is not RunwayIdentifier arrivalRunway)
        {
            return AtcDecision.Reject(
                "No arrival runway is available.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        RunwayData? runwayData = airport.FindRunway(arrivalRunway);
        if (runwayData is null)
        {
            return AtcDecision.Reject(
                $"Runway {arrivalRunway} does not exist at {airport.Icao}.",
                Say(context).Unable(callsign, UnableReason.RunwayNotAvailable));
        }

        // The approach name comes from published data, never from the pilot's request alone.
        string approachName = intent.Entities.ApproachName is { Length: > 0 } requested
            && runwayData.PublishesApproach(requested)
                ? requested
                : runwayData.Approaches.Count > 0 ? runwayData.Approaches[0] : "ILS";

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.ApproachClearance,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            Runway = arrivalRunway,
            ApproachName = approachName,
            RequiredReadbackItems = [ReadbackItemType.Runway, ReadbackItemType.ApproachName],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.ApproachClearance,
            NewPhase = FlightPhase.Approach,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.ApproachClearance,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Runway,
                        ExpectedValue = arrivalRunway.ToComparisonValue(),
                        SpokenValue = Say(context).SpeakRunway(arrivalRunway),
                        IsCritical = true,
                    },
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.ApproachName,
                        ExpectedValue = approachName,
                        SpokenValue = Say(context).ReadbackItemApproach(approachName),
                        IsCritical = false,
                    },
                ],
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = clearance.ToRequiredFacts(),

            // The single most important prohibition in the whole system: an approach clearance
            // is not a landing clearance, and a model must never blur the two.
            ProhibitedFacts =
            [
                "landing clearance",
                "cleared to land",
                "any runway other than the approved runway",
            ],
            FallbackPhraseology = Say(context).ApproachClearance(
                callsign, approachName, arrivalRunway),
        };
    }

    private static AtcDecision HandleEstablishedReport(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (session.ArrivalClearance is not { Type: ClearanceType.ApproachClearance } approachClearance)
        {
            return AtcDecision.Reject(
                "The aircraft has not been cleared for an approach.",
                Say(context).Unable(callsign, UnableReason.NotClearedForApproach));
        }

        // Reporting established on a runway other than the cleared one means the aircraft is
        // lined up on the wrong final. That must be caught, not acknowledged.
        if (intent.Entities.Runway is RunwayIdentifier reported
            && approachClearance.Runway is RunwayIdentifier cleared
            && reported != cleared)
        {
            return AtcDecision.Reject(
                $"Pilot reported established on runway {reported} but is cleared for runway {cleared}.",
                Say(context).WrongApproachRunway(callsign, cleared));
        }

        Frequency? towerFrequency = ResolveFrequency(context, ControllerPosition.Tower);

        if (session.CurrentController != ControllerPosition.Tower && towerFrequency is Frequency tower)
        {
            return BuildHandoff(
                context,
                ControllerPosition.Tower,
                tower,
                FlightPhase.Final,
                "Established on the approach.",
                Say(context).Handoff(callsign, ControllerPosition.Tower, tower));
        }

        return HandleLandingClearance(context, callsign);
    }

    /// <summary>
    /// Issues a landing clearance, which is a separate permission from the approach clearance.
    /// </summary>
    private static AtcDecision HandleLandingClearance(
        AtcEvaluationContext context,
        Callsign callsign,
        bool touchAndGo = false,
        CircuitManoeuvre manoeuvre = CircuitManoeuvre.None)
    {
        AtcSession session = context.Session;

        if (session.CurrentController != ControllerPosition.Tower)
        {
            return AtcDecision.Reject(
                "Landing clearance requires Tower.",
                Say(context).Unable(callsign, UnableReason.ContactTower));
        }

        RunwayIdentifier? runway = session.ArrivalClearance?.Runway ?? session.AssignedArrivalRunway;

        if (runway is not RunwayIdentifier landingRunway)
        {
            return AtcDecision.Reject(
                "No arrival runway has been assigned.",
                Say(context).Unable(callsign, UnableReason.NoLandingRunwayAssigned));
        }

        if (IsRunwayOccupied(context, landingRunway))
        {
            return AtcDecision.Reject(
                $"Runway {landingRunway} is occupied.",
                Say(context).ContinueApproachTrafficOnRunway(callsign));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.LandingClearance,
            IssuedBy = ControllerPosition.Tower,
            IssuedAt = context.CurrentTime,
            Runway = landingRunway,
            TouchAndGo = touchAndGo,
            RequiredReadbackItems = [ReadbackItemType.Runway],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = manoeuvre switch
            {
                CircuitManoeuvre.Option => AtcResponseType.OptionClearance,
                CircuitManoeuvre.LowPass => AtcResponseType.LowPassClearance,
                _ => touchAndGo ? AtcResponseType.TouchAndGoClearance : AtcResponseType.LandingClearance,
            },
            NewPhase = FlightPhase.Final,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.LandingClearance,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Runway,
                        ExpectedValue = landingRunway.ToComparisonValue(),
                        SpokenValue = Say(context).SpeakRunway(landingRunway),
                        IsCritical = true,
                    },
                ],
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = BuildLandingFacts(context, landingRunway, touchAndGo),
            ProhibitedFacts = ["takeoff clearance", "any runway other than the assigned runway"],
            FallbackPhraseology = manoeuvre == CircuitManoeuvre.Option
                ? Say(context).OptionClearance(
                    callsign,
                    landingRunway,
                    context.Weather?.WindDirection,
                    context.Weather?.WindSpeedKnots)
                : manoeuvre == CircuitManoeuvre.LowPass
                ? Say(context).LowPassClearance(
                    callsign,
                    landingRunway,
                    context.Weather?.WindDirection,
                    context.Weather?.WindSpeedKnots)
                : touchAndGo
                ? Say(context).TouchAndGoClearance(
                    callsign,
                    landingRunway,
                    context.Weather?.WindDirection,
                    context.Weather?.WindSpeedKnots)
                : Say(context).LandingClearance(
                    callsign,
                    landingRunway,
                    context.Weather?.WindDirection,
                    context.Weather?.WindSpeedKnots),
        };
    }

    /// <summary>
    /// "Holding short runway 27, request to cross": cleared across the runway the taxi clearance holds
    /// the aircraft short of, if nothing is on it or about to land on it.
    /// </summary>
    private static AtcDecision HandleRunwayCrossingRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "A runway is crossed on the ground.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (session.CurrentController is not (ControllerPosition.Ground or ControllerPosition.Tower))
        {
            return AtcDecision.Reject(
                $"A runway crossing is cleared by Ground or Tower; the aircraft is with {session.CurrentController}.",
                Say(context).Unable(callsign, UnableReason.NotWithThisController));
        }

        // The departure runway's holding point is not a crossing: the aircraft is there to depart.
        Clearance? taxi = session.TaxiClearance;
        if (taxi?.HoldShortOfRunway is not RunwayIdentifier holding
            || (taxi.Type == ClearanceType.Taxi && holding == session.AssignedDepartureRunway))
        {
            return AtcDecision.Reject(
                "No runway crossing is planned on the taxi route.",
                Say(context).Unable(callsign, UnableReason.NoRunwayCrossingPending));
        }

        if (intent.Entities.Runway is RunwayIdentifier asked
            && asked != holding
            && asked != holding.Reciprocal())
        {
            return AtcDecision.Reject(
                $"The aircraft holds short of {holding}, not {asked}.",
                Say(context).WrongHoldingPoint(callsign, holding));
        }

        if (IsRunwayOccupied(context, holding))
        {
            return AtcDecision.Reject(
                $"Runway {holding} is occupied.",
                Say(context).HoldShortTrafficOnRunway(callsign, holding));
        }

        if (FindTrafficOnShortFinal(context, holding) is not null)
        {
            return AtcDecision.Reject(
                $"Traffic is on short final for runway {holding}.",
                Say(context).HoldPositionTrafficOnShortFinal(callsign, holding));
        }

        // The crossing replaces the hold-short restriction but keeps the route, so progressive taxi
        // goes on from the far side of the runway.
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.RunwayCrossing,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            Runway = holding,
            TaxiRoute = taxi.TaxiRoute,
            TaxiPathNodes = taxi.TaxiPathNodes,
            TaxiPathTaxiways = taxi.TaxiPathTaxiways,
            TaxiRouteLengthMeters = taxi.TaxiRouteLengthMeters,
            TaxiDestination = taxi.TaxiDestination,
            RequiredReadbackItems = [ReadbackItemType.Runway],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.RunwayCrossingClearance,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.RunwayCrossing,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Runway,
                        ExpectedValue = holding.ToComparisonValue(),
                        SpokenValue = Say(context).SpeakRunway(holding),
                        IsCritical = true,
                    },
                ],
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = "cross the runway",
                ["runway"] = holding.ToComparisonValue(),
            },
            ProhibitedFacts = ["takeoff clearance", "line up", "any runway other than the one named"],
            FallbackPhraseology = Say(context).RunwayCrossingClearance(callsign, holding),
        };
    }

    private static AtcDecision HandleGoAround(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        // A go-around is always approved. Refusing one would be nonsense: the aircraft is
        // already going around by the time the pilot says so. It is answered by whoever has the
        // aircraft: Tower on short final, Approach when the approach is missed earlier.
        int elevation = (context.DestinationAirport ?? context.DepartureAirport)?.ElevationFeet ?? 0;
        Altitude climbTo = session.ClearedAltitude ?? DefaultMissedApproachAltitude(elevation);

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Climb,
            IssuedBy = session.CurrentController,
            IssuedAt = context.CurrentTime,
            InitialAltitude = climbTo,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.GoAroundInstruction,
            NewPhase = FlightPhase.GoAround,
            IssuedClearance = clearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = "go around",
                ["climbAltitude"] = climbTo.ToComparisonValue(),
            },
            ProhibitedFacts = ["landing clearance", "traffic information"],
            FallbackPhraseology = Say(context).GoAroundInstruction(callsign, climbTo, heading: null),
        };
    }

    private static AtcDecision HandleRunwayVacated(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        Frequency? groundFrequency = ResolveFrequency(context, ControllerPosition.Ground);

        if (session.CurrentController == ControllerPosition.Tower && groundFrequency is Frequency ground)
        {
            return BuildHandoff(
                context,
                ControllerPosition.Ground,
                ground,
                FlightPhase.Landed,
                "Runway vacated after landing.",
                Say(context).Handoff(callsign, ControllerPosition.Ground, ground));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewPhase = FlightPhase.Landed,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = "roger",
            },
            ProhibitedFacts = ["taxi clearance"],
            FallbackPhraseology = Say(context).Acknowledgement(callsign),
        };
    }

    private static AtcDecision HandleTaxiToStandRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (session.Phase is not (FlightPhase.Landed or FlightPhase.TaxiIn))
        {
            return AtcDecision.Reject(
                $"Taxi to stand is not available in the {session.Phase} phase.",
                Say(context).Unable(callsign, UnableReason.NotLanded));
        }

        if (context.DestinationAirport is not AirportData airport)
        {
            return AtcDecision.Reject(
                "No airport data is available for the destination.",
                Say(context).Unable(callsign, UnableReason.AerodromeDataUnavailable));
        }

        RunwayIdentifier? landingRunway = session.AssignedArrivalRunway
            ?? session.ArrivalClearance?.Runway;

        // The stand has to exist at the arrival aerodrome. The session's assigned stand is the
        // departure one until this clearance replaces it, so it is only usable here if the
        // destination happens to publish a stand by the same name.
        string standName = ExistingStand(airport, intent.Entities.Stand)
            ?? ExistingStand(airport, session.AssignedStand)
            ?? (airport.Stands.Count > 0 ? airport.Stands[0].Name : null)
            ?? string.Empty;

        if (standName.Length == 0 || landingRunway is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No stand or landing runway is known.",
                Say(context).Unable(callsign, UnableReason.NoStandAvailable));
        }

        if (!airport.SupportsTaxiRouting)
        {
            return AtcDecision.Reject(
                "The aerodrome has no taxi graph.",
                Say(context).TaxiToStandNoRoute(callsign, standName));
        }

        TaxiRoute route = TaxiPathfinder.FindRouteFromPositionToStand(
            airport,
            context.AircraftState.Position,
            standName);

        if (!route.Found)
        {
            route = TaxiPathfinder.FindRouteFromRunwayToStand(airport, runway, standName);
        }

        if (!route.Found)
        {
            return AtcDecision.Reject(
                $"No taxi route exists from runway {runway} to stand {standName}.",
                Say(context).TaxiToStandNoRoute(callsign, standName));
        }

        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.TaxiToStand,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = context.CurrentTime,
            TaxiRoute = route.TaxiwayNames,
            TaxiPathNodes = route.Nodes,
            TaxiPathTaxiways = route.Edges.Select(edge => edge.TaxiwayName).ToArray(),
            TaxiRouteLengthMeters = route.TotalLengthMeters,
            TaxiDestination = standName,
            HoldShortOfRunway = route.RequiresRunwayCrossing ? route.CrossedRunways[0] : null,
            RequiredReadbackItems = [ReadbackItemType.TaxiRoute],
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TaxiToStandClearance,
            NewPhase = FlightPhase.TaxiIn,
            IssuedClearance = clearance,
            RequiredReadback = new PendingReadback
            {
                ClearanceId = clearance.ClearanceId,
                ClearanceType = ClearanceType.TaxiToStand,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.TaxiRoute,
                        ExpectedValue = string.Join(",", route.TaxiwayNames),
                        SpokenValue = Say(context).ReadbackItemTaxiRoute(route.TaxiwayNames),
                        IsCritical = true,
                    },
                ],
                IssuedAt = context.CurrentTime,
                IsMandatory = true,
            },
            ApprovedFacts = clearance.ToRequiredFacts(),
            ProhibitedFacts = ["any taxiway not listed in the approved taxi route", "runway crossing not listed"],
            FallbackPhraseology = Say(context).TaxiToStandClearance(callsign, clearance),
        };
    }

    // =====================================================================================
    // Guards and helpers
    // =====================================================================================

    /// <summary>
    /// Rejects transmissions sent to a controller that does not handle that request.
    /// </summary>
    private static AtcDecision? CheckJurisdiction(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;
        ControllerPosition current = session.CurrentController;

        // Before the first contact any position may be addressed.
        if (current == ControllerPosition.None)
        {
            return null;
        }

        ControllerPosition? required = RequiredControllerFor(intent.Type);
        if (required is not ControllerPosition needed || needed == current)
        {
            return null;
        }

        // A combined position covers the roles it absorbs — at a quiet aerodrome Tower may work
        // ground and delivery too. Only reject when the aerodrome actually staffs them apart.
        if (IsCoveredByCombinedPosition(context, current, needed))
        {
            return null;
        }

        return AtcDecision.Reject(
            $"{intent.Type} is handled by {needed}, but the aircraft is with {current}.",
            Say(context).ContactController(session.Callsign, needed));
    }

    private static ControllerPosition? RequiredControllerFor(PilotIntentType intent) => intent switch
    {
        PilotIntentType.RequestIFRClearance => ControllerPosition.ClearanceDelivery,
        PilotIntentType.RequestPushback or PilotIntentType.RequestTaxi
            or PilotIntentType.RequestProgressiveTaxi
            or PilotIntentType.RequestTaxiToStand => ControllerPosition.Ground,
        PilotIntentType.ReadyForDeparture
            or PilotIntentType.VfrArrivalCall
            or PilotIntentType.ReportAtPoint
            or PilotIntentType.ReportDownwind
            or PilotIntentType.ReportBase
            or PilotIntentType.ReportFinal
            or PilotIntentType.RequestTouchAndGo
            or PilotIntentType.LeaveFrequency => ControllerPosition.Tower,
        _ => null,
    };

    /// <summary>
    /// True when the aerodrome does not publish a separate frequency for the required position,
    /// meaning the current controller is working it as a combined position.
    /// </summary>
    private static bool IsCoveredByCombinedPosition(
        AtcEvaluationContext context,
        ControllerPosition current,
        ControllerPosition required)
    {
        AirportFrequencies? frequencies = context.RelevantAirport?.Frequencies;
        if (frequencies is null)
        {
            // Imported plans commonly have a runway but no bundled local frequency file. In
            // that case Delivery safely covers Ground instead of ordering the pilot to contact
            // an unnamed frequency. Tower remains separate because runway control must never
            // be silently folded into a position just because data is missing.
            return (current, required) is
                (ControllerPosition.ClearanceDelivery, ControllerPosition.Ground)
                or (ControllerPosition.Ground, ControllerPosition.ClearanceDelivery);
        }

        // If the required position has no published frequency of its own, whoever the pilot is
        // talking to is covering it.
        if (frequencies.For(required) is null)
        {
            return true;
        }

        // Delivery folding into Ground is the common real-world combination.
        return (current, required) is
            (ControllerPosition.Ground, ControllerPosition.ClearanceDelivery)
            or (ControllerPosition.Tower, ControllerPosition.Ground)
            or (ControllerPosition.Tower, ControllerPosition.ClearanceDelivery);
    }

    private static bool IsReadbackIntent(PilotIntentType type) => type is
        PilotIntentType.ClearanceReadback
        or PilotIntentType.TaxiReadback
        or PilotIntentType.TakeoffReadback
        or PilotIntentType.LandingReadback
        or PilotIntentType.AltitudeReadback
        or PilotIntentType.HeadingReadback
        or PilotIntentType.FrequencyReadback
        or PilotIntentType.Correction
        or PilotIntentType.SayAgain;

    private static string BuildReadbackReminder(AtcEvaluationContext context)
    {
        return Say(context).ReadbackReminder(context.Session.Callsign);
    }

    /// <summary>
    /// Detects the aircraft crossing onto a runway it was told to hold short of.
    /// </summary>
    /// <remarks>
    /// This is simulation logic only. It exists so that a pilot practising discipline gets
    /// feedback, not as any kind of safety protection — StormATC has no connection to real
    /// traffic and makes no claim to detect real conflicts.
    /// </remarks>
    private static AtcDecision? CheckRunwayIncursion(AtcEvaluationContext context)
    {
        if (!context.Realism.EnforceHoldShort)
        {
            return null;
        }

        AtcSession session = context.Session;
        Aircraft.AircraftState aircraft = context.AircraftState;

        if (!aircraft.IsOnGround || aircraft.GroundSpeedKnots < 1)
        {
            return null;
        }

        // A hold-short restriction only exists while a taxi clearance carries one.
        if (session.TaxiClearance?.HoldShortOfRunway is not RunwayIdentifier holdShort)
        {
            return null;
        }

        // Once cleared for takeoff or to line up, entering the runway is exactly what was asked.
        if (session.DepartureClearance?.Type is ClearanceType.Takeoff or ClearanceType.LineUp
            || session.Phase is FlightPhase.LineUp or FlightPhase.Takeoff or FlightPhase.Landed
                or FlightPhase.TaxiIn)
        {
            return null;
        }

        if (context.RelevantAirport?.FindRunway(holdShort) is not RunwayData runway)
        {
            return null;
        }

        double distance = DistanceToRunwayCenterlineMeters(aircraft.Position, runway);
        if (distance > RunwayIncursionThresholdMeters)
        {
            return null;
        }

        // Already warned about this runway — do not spam a warning every state update.
        bool alreadyWarned = session.ActiveWarnings.Any(w =>
            string.Equals(w.Code, "HoldShortViolation", StringComparison.Ordinal));

        if (alreadyWarned)
        {
            return null;
        }

        return new AtcDecision
        {
            Accepted = false,
            ResponseType = AtcResponseType.Rejection,
            RejectionReason = $"Aircraft entered runway {holdShort} without clearance.",
            Warnings =
            [
                new AtcWarning
                {
                    Severity = AtcSeverity.Warning,
                    Code = "HoldShortViolation",
                    Message = $"Simulated hold-short violation: the aircraft entered runway {holdShort.ToDisplay()} without clearance.",
                    RaisedAt = context.CurrentTime,
                },
            ],
            FallbackPhraseology = Say(context).HoldShortViolation(session.Callsign, holdShort),
            AllowLanguageModelPhraseology = false,
        };
    }

    /// <summary>
    /// Perpendicular distance from a point to the runway centreline segment, in metres.
    /// </summary>
    /// <remarks>
    /// Uses a local flat-earth projection: over a runway's few kilometres the curvature error
    /// is far below the 40 m threshold this feeds, and the closed-form segment projection is
    /// much easier to verify than a spherical equivalent.
    /// </remarks>
    private static double DistanceToRunwayCenterlineMeters(GeoPosition point, RunwayData runway)
    {
        // Convert to a local metre grid centred on the threshold.
        const double MetersPerDegreeLatitude = 111_320.0;
        double metersPerDegreeLongitude =
            MetersPerDegreeLatitude * Math.Cos(runway.ThresholdPosition.Latitude * Math.PI / 180.0);

        double ax = 0;
        double ay = 0;
        double bx = (runway.EndPosition.Longitude - runway.ThresholdPosition.Longitude) * metersPerDegreeLongitude;
        double by = (runway.EndPosition.Latitude - runway.ThresholdPosition.Latitude) * MetersPerDegreeLatitude;
        double px = (point.Longitude - runway.ThresholdPosition.Longitude) * metersPerDegreeLongitude;
        double py = (point.Latitude - runway.ThresholdPosition.Latitude) * MetersPerDegreeLatitude;

        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared < 1e-6)
        {
            return Math.Sqrt((px * px) + (py * py));
        }

        // Project the point onto the segment and clamp to the endpoints.
        double t = Math.Clamp(((px * dx) + (py * dy)) / lengthSquared, 0, 1);
        double closestX = t * dx;
        double closestY = t * dy;

        double offsetX = px - closestX;
        double offsetY = py - closestY;

        return Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
    }

    /// <summary>Distance from a threshold inside which traffic on the extended centreline is on short final.</summary>
    private const double ShortFinalNauticalMiles = 3.0;

    /// <summary>Height above the aerodrome below which traffic on the centreline is landing, not overflying.</summary>
    private const int ShortFinalHeightFeet = 1500;

    /// <summary>How far, in degrees, traffic may be off the extended centreline and still be on final.</summary>
    private const int ShortFinalToleranceDegrees = 20;

    /// <summary>
    /// Moving traffic close to the extended centreline of the runway, at either end, and low
    /// enough to be landing on it. Traffic on the runway itself is <see cref="IsRunwayOccupied"/>'s concern.
    /// </summary>
    private static TrafficContact? FindTrafficOnShortFinal(AtcEvaluationContext context, RunwayIdentifier runway)
    {
        AirportData? airport = context.RelevantAirport;
        if (airport?.FindRunway(runway) is not RunwayData data)
        {
            return null;
        }

        Heading runwayDirection = data.CenterlineBearing;
        int ceiling = airport.ElevationFeet + ShortFinalHeightFeet;

        foreach (TrafficContact contact in context.Traffic)
        {
            if (contact.IsOnRunway || contact.GroundSpeedKnots < 40 || contact.AltitudeFeet > ceiling)
            {
                continue;
            }

            // Behind the threshold: traffic landing on this runway. Beyond the far end: traffic landing the other way.
            bool landingSameWay =
                data.ThresholdPosition.DistanceNauticalMilesTo(contact.Position) <= ShortFinalNauticalMiles
                && data.ThresholdPosition.TrueBearingTo(contact.Position).DifferenceTo(Opposite(runwayDirection))
                    <= ShortFinalToleranceDegrees;
            bool landingOtherWay =
                data.EndPosition.DistanceNauticalMilesTo(contact.Position) <= ShortFinalNauticalMiles
                && data.EndPosition.TrueBearingTo(contact.Position).DifferenceTo(runwayDirection)
                    <= ShortFinalToleranceDegrees;

            if (landingSameWay || landingOtherWay)
            {
                return contact;
            }
        }

        return null;
    }

    private static Heading Opposite(Heading heading) => Heading.FromDegrees((heading.Degrees + 180) % 360);

    private static bool IsRunwayOccupied(AtcEvaluationContext context, RunwayIdentifier runway)
    {
        foreach (TrafficContact contact in context.Traffic)
        {
            if (contact.IsOnRunway
                && (contact.OccupiedRunway == runway || contact.OccupiedRunway == runway.Reciprocal()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The altitude to climb to on a go-around when none was assigned: 2000 ft above the aerodrome,
    /// to the next 500 ft, and never below 3000 ft.
    /// </summary>
    private static Altitude DefaultMissedApproachAltitude(int elevationFeet)
    {
        int rounded = (elevationFeet + 2000 + 499) / 500 * 500;
        return Altitude.FromFeet(Math.Max(3000, rounded));
    }

    private static bool IsAboveAirportBy(AirportData? airport, Aircraft.AircraftState aircraft, int feet) =>
        aircraft.IndicatedAltitudeFeet > (airport?.ElevationFeet ?? 0) + feet;

    private static bool IsAboveAerodromeBy(AtcEvaluationContext context, int feet)
    {
        int elevation = context.DepartureAirport?.ElevationFeet ?? 0;
        return context.AircraftState.IndicatedAltitudeFeet > elevation + feet;
    }

    /// <summary>
    /// Radius, in nautical miles, inside which an arriving aircraft is treated as being in the
    /// destination's terminal area.
    /// </summary>
    /// <remarks>
    /// A single radius stands in for published terminal airspace, which would need procedure
    /// data StormATC does not bundle. Forty miles is roughly where a real arrival is handed to
    /// approach control, and it is generous enough that the handoff never fails to happen.
    /// </remarks>
    private const double TerminalAreaRadiusNauticalMiles = 40;

    /// <summary>Returns the stand name only if that aerodrome actually publishes it.</summary>
    private static string? ExistingStand(AirportData airport, string? standName) =>
        standName is { Length: > 0 } name && airport.FindStand(name) is not null ? name : null;

    /// <summary>True when the aircraft is close enough to its destination for Approach to own it.</summary>
    private static bool IsWithinTerminalArea(AtcEvaluationContext context)
    {
        if (context.DestinationAirport is not AirportData destination)
        {
            return false;
        }

        return context.AircraftState.Position.IsWithin(
            destination.ReferencePosition, TerminalAreaRadiusNauticalMiles);
    }

    private static bool IsAtAssignedStand(AtcEvaluationContext context)
    {
        if (context.Session.AssignedStand is not { Length: > 0 } standName
            || context.RelevantAirport?.FindStand(standName) is not StandData stand)
        {
            // With no stand assigned, being stationary with the brake set is enough.
            return true;
        }

        return context.AircraftState.Position.IsWithin(stand.Position, ProximityNauticalMiles);
    }

    private static string BuildVacateInstruction(AtcEvaluationContext context)
    {
        Callsign? callsign = context.Session.Callsign;
        if (callsign is null)
        {
            return Say(context).VacateWhenAbleNoCallsign;
        }

        RunwayIdentifier? runway = context.Session.AssignedArrivalRunway;
        string? exit = null;

        // Name an exit only if the aerodrome data actually publishes one.
        if (runway is RunwayIdentifier landing
            && context.RelevantAirport?.FindRunway(landing) is RunwayData runwayData
            && runwayData.ExitNodes.Count > 0)
        {
            TaxiNode? exitNode = context.RelevantAirport.TaxiGraph?.FindNode(runwayData.ExitNodes[0]);

            // Use the taxiway that the exit edge belongs to, not the node name.
            exit = context.RelevantAirport.TaxiGraph?.Edges
                .FirstOrDefault(e =>
                    string.Equals(e.FromNode, exitNode?.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.ToNode, exitNode?.Name, StringComparison.OrdinalIgnoreCase))
                ?.TaxiwayName;
        }

        return Say(context).VacateInstruction(callsign, exit);
    }

    private static AtcDecision BuildHandoff(
        AtcEvaluationContext context,
        ControllerPosition toController,
        Frequency frequency,
        FlightPhase? newPhase,
        string reason,
        string fallbackPhraseology)
    {
        var handoff = new ControllerHandoff
        {
            FromController = context.Session.CurrentController,
            ToController = toController,
            ToFrequency = frequency,
            InstructedAt = context.CurrentTime,
            State = HandoffState.Instructed,
            Reason = reason,
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Handoff,
            NewPhase = newPhase,
            Handoff = handoff,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nextController"] = Say(context).ControllerName(toController),
                ["frequency"] = frequency.ToComparisonValue(),
            },
            ProhibitedFacts = ["any clearance", "any altitude change"],
            FallbackPhraseology = fallbackPhraseology,
        };
    }

    private static PendingReadback BuildAltitudeReadback(
        AtcEvaluationContext context,
        Clearance clearance,
        Altitude altitude,
        bool isClimb) => new()
        {
            ClearanceId = clearance.ClearanceId,
            ClearanceType = clearance.Type,
            ExpectedItems =
            [
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.CruiseAltitude,
                    ExpectedValue = altitude.ToComparisonValue(),
                    SpokenValue = Say(context).ReadbackItemAltitudeChange(altitude, isClimb),
                    IsCritical = true,
                },
            ],
            IssuedAt = context.CurrentTime,
            IsMandatory = true,
        };

    private static Dictionary<string, string> BuildTakeoffFacts(
        AtcEvaluationContext context,
        RunwayIdentifier runway)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runway"] = runway.ToComparisonValue(),
            ["clearance"] = "cleared for takeoff",
        };

        AddWindFacts(context, facts);
        return facts;
    }

    private static Dictionary<string, string> BuildLandingFacts(
        AtcEvaluationContext context,
        RunwayIdentifier runway,
        bool touchAndGo = false)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runway"] = runway.ToComparisonValue(),
            ["clearance"] = touchAndGo ? "cleared touch and go" : "cleared to land",
        };

        AddWindFacts(context, facts);
        return facts;
    }

    /// <summary>
    /// Adds wind to the approved facts only when a weather source actually provided it. With no
    /// provider configured the response simply omits wind rather than inventing a plausible one.
    /// </summary>
    private static void AddWindFacts(AtcEvaluationContext context, Dictionary<string, string> facts)
    {
        if (context.Weather is { HasWind: true } weather)
        {
            facts["windDirection"] = weather.WindDirection!.Value.ToComparisonValue();
            facts["windSpeed"] = weather.WindSpeedKnots!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static List<ReadbackItemType> BuildClearanceReadbackItemTypes(Frequency? departureFrequency)
    {
        var items = new List<ReadbackItemType>
        {
            ReadbackItemType.Callsign,
            ReadbackItemType.Runway,
            ReadbackItemType.InitialAltitude,
            ReadbackItemType.Squawk,
        };

        if (departureFrequency is not null)
        {
            items.Add(ReadbackItemType.Frequency);
        }

        return items;
    }

    private static Altitude ResolveInitialAltitude(
        AtcEvaluationContext context,
        FlightPlanning.FlightPlan plan)
    {
        // A published SID initial altitude would come from procedure data StormATC does not
        // bundle. Until it does, use a conservative default well below any filed cruise level.
        const int DefaultInitialClimbFeet = 7000;

        int elevation = context.DepartureAirport?.ElevationFeet ?? 0;
        int candidate = DefaultInitialClimbFeet;

        if (plan.CruiseAltitude is Altitude cruise && cruise.Feet < candidate)
        {
            candidate = Math.Max(cruise.Feet, elevation + 2000);
        }

        return Altitude.FromFeet(candidate);
    }

    /// <summary>
    /// Chooses the transponder code for a clearance.
    /// </summary>
    /// <remarks>
    /// A pre-assigned code from the scenario data wins, so the sample flight is reproducible.
    /// Otherwise a code is derived deterministically from the callsign: octal digits only, and
    /// never one of the reserved emergency codes.
    /// </remarks>
    private static SquawkCode ResolveSquawk(AtcEvaluationContext context, Callsign callsign)
    {
        if (context.PreassignedSquawk is SquawkCode assigned)
        {
            return assigned;
        }

        // Stable hash over the normalised callsign — deliberately not string.GetHashCode(),
        // which is randomised per process and would make sessions irreproducible.
        int hash = 17;
        foreach (char c in callsign.Normalized)
        {
            hash = unchecked((hash * 31) + c);
        }

        hash = Math.Abs(hash);

        // Build four octal digits, keeping the first digit in 1-6 so the result can never be a
        // reserved 75xx/76xx/77xx code or the 0000 conspicuity code.
        int d1 = (hash % 6) + 1;
        int d2 = (hash / 6) % 8;
        int d3 = (hash / 48) % 8;
        int d4 = (hash / 384) % 8;

        return SquawkCode.Create($"{d1}{d2}{d3}{d4}");
    }

    private static Frequency? ResolveFrequency(AtcEvaluationContext context, ControllerPosition position) =>
        context.RelevantAirport?.Frequencies.ResolveWithFallback(position);

    /// <summary>The phraseology profile for the session's radio language.</summary>
    private static IAtcPhraseology Say(AtcEvaluationContext context) =>
        AtcPhraseologies.For(context.Session.Language);
}
