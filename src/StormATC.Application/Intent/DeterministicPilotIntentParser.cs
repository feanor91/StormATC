using StormATC.Domain;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Intent;

/// <summary>
/// The fast local parser. Recognises standard phraseology with no network call.
/// </summary>
/// <remarks>
/// <para>
/// Most real transmissions are formulaic — "request IFR clearance", "ready for departure",
/// "holding short runway one eight". Matching those locally keeps the common path at
/// single-digit milliseconds and, more importantly, keeps the language model out of the loop
/// entirely for the transmissions that matter most.
/// </para>
/// <para>
/// The parser reports honest confidence. A transmission matching a distinctive keyword phrase
/// scores high; one matched only by a weak signal scores low, and the pipeline escalates to
/// the language model rather than guessing. Silence about uncertainty would be worse than
/// being slow.
/// </para>
/// </remarks>
public sealed partial class DeterministicPilotIntentParser : IPilotIntentParser
{
    /// <summary>
    /// Intent patterns in priority order. The first matching rule wins, so more specific
    /// phrases must come before the general ones they contain.
    /// </summary>
    /// <remarks>
    /// Ordering carries real meaning here. "ready for departure" must be tested before
    /// "ready", and "going around" before any landing-related rule, because a go-around
    /// mentioned during an approach must never be read as an approach report.
    /// </remarks>
    private static readonly IntentRule[] Rules =
    [
        // --- Emergencies first. Nothing may shadow these. -------------------------------
        new(PilotIntentType.Mayday, 0.99, ["mayday"]),
        new(PilotIntentType.PanPan, 0.99, ["panpan", "pan pan"]),

        // --- Go-around, before anything approach-related. --------------------------------
        new(PilotIntentType.GoAround, 0.97, ["going around", "go around", "going missed", "missed approach"]),

        // --- Explicit conversational controls. -------------------------------------------
        new(PilotIntentType.SayAgain, 0.95, ["say again", "repeat that", "repeat please"]),
        new(PilotIntentType.Unable, 0.95, ["unable"]),
        new(PilotIntentType.Correction, 0.9, ["correction"]),
        new(PilotIntentType.RadioCheck, 0.95, ["radio check", "how do you read", "read you"]),
        new(PilotIntentType.RequestFrequency, 0.96,
            ["what s the frequency", "what is the frequency", "which frequency",
             "frequency please", "ground frequency", "tower frequency",
             "departure frequency please", "approach frequency please"]),

        // --- Clearance delivery. ----------------------------------------------------------
        new(PilotIntentType.RequestIFRClearance, 0.96,
            ["request ifr clearance", "request clearance", "ifr clearance", "ready to copy",
             "request ifr", "clearance request"]),

        // --- Ground. ----------------------------------------------------------------------
        new(PilotIntentType.RequestPushback, 0.96,
            ["request pushback", "ready for pushback", "request push", "ready for push", "pushback request"]),
        new(PilotIntentType.RequestTaxiToStand, 0.94,
            ["taxi to stand", "taxi to gate", "request stand", "request gate", "taxi to the stand"]),
        new(PilotIntentType.RequestProgressiveTaxi, 0.97,
            ["request progressive taxi", "progressive taxi", "progressive instructions",
             "unsure of the taxi route", "lost while taxiing", "where do i taxi"]),
        new(PilotIntentType.RequestTaxi, 0.95,
            ["request taxi", "ready for taxi", "ready to taxi", "taxi request"]),

        // --- Tower. -----------------------------------------------------------------------
        // Before "holding short": a pilot who holds short of a runway and asks to cross it says both.
        new(PilotIntentType.RequestRunwayCrossing, 0.96,
            ["request to cross", "request crossing", "request cross", "ready to cross", "permission to cross"]),
        new(PilotIntentType.ReportHoldingShort, 0.95,
            ["holding short", "hold short of", "at the holding point", "holding point"]),
        new(PilotIntentType.ReadyForDeparture, 0.95,
            ["ready for departure", "ready for takeoff", "ready to depart", "ready for the roll"]),
        new(PilotIntentType.ReportRunwayVacated, 0.94,
            ["runway vacated", "vacated the runway", "clear of the runway", "runway is clear"]),
        new(PilotIntentType.ReportAirborne, 0.9, ["airborne", "we are off", "off the ground"]),

        // --- Radar. -----------------------------------------------------------------------
        new(PilotIntentType.ReportEstablished, 0.95,
            ["established", "established on the", "localizer established", "on the localizer"]),
        new(PilotIntentType.RequestApproach, 0.93,
            ["request approach", "request the ils", "request vectors for", "request the approach"]),
        new(PilotIntentType.RequestDirect, 0.93, ["request direct", "direct to", "can we go direct"]),
        new(PilotIntentType.RequestDeviation, 0.92,
            ["request deviation", "deviate", "request weather deviation", "deviation for weather"]),
        new(PilotIntentType.RequestClimb, 0.92, ["request climb", "request higher", "requesting climb"]),
        new(PilotIntentType.RequestDescent, 0.92, ["request descent", "request lower", "requesting descent"]),
        new(PilotIntentType.CheckIn, 0.88,
            ["checking in", "with you", "good day", "passing", "climbing through"]),

        // --- Readbacks. Weaker signals, resolved by context below. ------------------------
        new(PilotIntentType.ClearanceReadback, 0.75, ["cleared to", "squawk"]),
        new(PilotIntentType.TaxiReadback, 0.7, ["taxi via", "via alpha", "via bravo", "via charlie"]),
        new(PilotIntentType.TakeoffReadback, 0.8, ["cleared for takeoff", "cleared for take off"]),
        new(PilotIntentType.LandingReadback, 0.8, ["cleared to land"]),
        new(PilotIntentType.AltitudeReadback, 0.65, ["climbing", "descending", "maintaining", "leaving"]),
        new(PilotIntentType.HeadingReadback, 0.7, ["heading"]),
        new(PilotIntentType.FrequencyReadback, 0.7, ["contact", "over to", "switching to"]),

        // --- Weakest: a bare initial call. -------------------------------------------------
        new(PilotIntentType.InitialCall, 0.6, ["good morning", "good afternoon", "good evening", "hello"]),
    ];

    /// <inheritdoc />
    public string ParserName => "Deterministic";

    /// <inheritdoc />
    public Task<PilotIntent> ParseAsync(
        string normalizedTranscript,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedTranscript);
        ArgumentNullException.ThrowIfNull(session);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Parse(normalizedTranscript, session));
    }

    /// <summary>Parses synchronously. Exposed for tests and for the coordinator's fast path.</summary>
    public static PilotIntent Parse(string normalizedTranscript, AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(normalizedTranscript);
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return PilotIntent.Unknown(normalizedTranscript, IntentParserSource.Deterministic);
        }

        Callsign? callsign = ExtractCallsign(normalizedTranscript, session);
        IntentEntities entities = VfrIntentRules.Enrich(
            ExtractEntities(normalizedTranscript),
            normalizedTranscript);

        (PilotIntentType type, double confidence) = MatchRule(normalizedTranscript, session);

        // Context refinement: a bare readback of numbers is ambiguous on its own, but if the
        // session is waiting for a specific readback, that is almost certainly what it is.
        (type, confidence) = RefineWithSessionContext(type, confidence, session, entities, normalizedTranscript);

        if (type == PilotIntentType.Unknown)
        {
            return new PilotIntent
            {
                Type = PilotIntentType.Unknown,
                Confidence = 0,
                Callsign = callsign,
                Entities = entities,
                Source = IntentParserSource.Deterministic,
                SourceTranscript = normalizedTranscript,
                RequiresClarification = true,
                ClarificationReason = "No known phraseology pattern matched.",
            };
        }

        // Having recognised our own callsign is corroboration; not having one is a small
        // penalty because ATC transmissions normally carry one.
        if (callsign is not null)
        {
            confidence = Math.Min(1.0, confidence + 0.04);
        }
        else if (session.Callsign is not null)
        {
            confidence = Math.Max(0.0, confidence - 0.10);
        }

        return new PilotIntent
        {
            Type = type,
            Confidence = Math.Round(confidence, 3),
            Callsign = callsign,
            Entities = entities,
            ReadbackItems = BuildReadbackItems(entities, normalizedTranscript),
            Source = IntentParserSource.Deterministic,
            SourceTranscript = normalizedTranscript,
        };
    }

    /// <summary>Finds the first matching rule.</summary>
    private static (PilotIntentType Type, double Confidence) MatchRule(string transcript, AtcSession session)
    {
        if (IsBriefAcknowledgement(transcript))
        {
            return (PilotIntentType.PilotAcknowledgement, 0.95);
        }

        // "Request CAM India" changes the military regime; it is not a circuit or IFR call.
        if (MilitaryIntentRules.AsksRegimeChange(transcript))
        {
            return (PilotIntentType.RequestRegimeChange, 0.94);
        }

        // A military pilot reporting the initial point, or the gear after "check gear down and locked".
        bool military = session.FlightRules.IsMilitary() || MilitaryIntentRules.RegimeIn(transcript) is not null;
        if (military && MilitaryIntentRules.AsksForTacticalPicture(transcript))
        {
            return (PilotIntentType.RequestTacticalPicture, 0.92);
        }

        if (military && MilitaryIntentRules.AsksForMarsa(transcript))
        {
            return (PilotIntentType.RequestMarsa, 0.94);
        }

        if (military && MilitaryIntentRules.AsksForRadarApproach(transcript))
        {
            return (PilotIntentType.RequestRadarApproach, 0.94);
        }

        if (military && MilitaryIntentRules.MentionsRunwayInSight(transcript))
        {
            return (PilotIntentType.ReportFinal, 0.92);
        }

        if (military && MilitaryIntentRules.IsInitialReport(transcript))
        {
            return (PilotIntentType.ReportInitial, 0.93);
        }

        if (military && MilitaryIntentRules.MentionsGear(transcript) && !transcript.Contains("final", StringComparison.Ordinal)
            && !transcript.Contains("base", StringComparison.Ordinal))
        {
            return (PilotIntentType.ReportFinal, 0.9);
        }

        // The everyday requests of the phraseology manual: backtrack, time, ident, special VFR, transit...
        if (ProcedureIntentRules.TryMatch(transcript, session) is (PilotIntentType procedureType, double procedureConfidence))
        {
            return (procedureType, procedureConfidence);
        }

        // VFR circuit and arrival calls are tested before the general rules: "passing e" would
        // otherwise be read as a check-in, and "downwind" has no general rule at all.
        if (VfrIntentRules.TryMatch(transcript, session) is (PilotIntentType vfrType, double vfrConfidence))
        {
            return (vfrType, vfrConfidence);
        }

        // "Holding point ... ready" is a pilot ready to depart, not one merely reporting position.
        if (session.AircraftState.IsOnGround
            && transcript.Contains("ready", StringComparison.Ordinal)
            && (transcript.Contains("holding", StringComparison.Ordinal)
                || transcript.Contains("hold short", StringComparison.Ordinal))
            && !transcript.Contains("ready for taxi", StringComparison.Ordinal)
            && !transcript.Contains("ready to taxi", StringComparison.Ordinal))
        {
            return (PilotIntentType.ReadyForDeparture, 0.9);
        }

        (PilotIntentType Type, double Confidence)? weak = null;
        foreach (IntentRule rule in Rules)
        {
            foreach (string phrase in rule.Phrases)
            {
                if (!transcript.Contains(phrase, StringComparison.Ordinal))
                {
                    continue;
                }

                // A greeting alone is a weak reading: if the stage of the flight says more, use that.
                if (rule.Confidence < 0.7 && weak is null)
                {
                    weak = (rule.Type, rule.Confidence);
                    break;
                }

                return (rule.Type, rule.Confidence);
            }

            if (weak is not null)
            {
                break;
            }
        }

        (PilotIntentType contextual, double contextualConfidence) = ContextualFallback(transcript, session);
        if (contextual != PilotIntentType.Unknown)
        {
            return (contextual, contextualConfidence);
        }

        return weak ?? (PilotIntentType.Unknown, 0);
    }

    /// <summary>
    /// What the pilot most plausibly means when no phrase matched exactly, read from the stage of
    /// the flight.
    /// </summary>
    /// <remarks>
    /// Pilots do not recite the manual: "VFR for a controlled departure", "taxi please", "level
    /// one eight zero". Rather than ask them to repeat until they use an example, the stage of the
    /// flight says what is being asked: at the parking it is a request to taxi, at the holding
    /// point it is readiness, in the climb it is a check-in or a level request. The engine still
    /// checks every condition (controller, runway, traffic) before it permits anything, so a loose
    /// reading can only produce a request, never a clearance. Readbacks are never read loosely.
    /// </remarks>
    private static (PilotIntentType Type, double Confidence) ContextualFallback(
        string transcript,
        AtcSession session)
    {
        static bool Mentions(string text, params string[] words) =>
            words.Any(word => text.Contains(word, StringComparison.Ordinal));

        bool vfr = session.FlightRules.IsVisual();

        if (session.AircraftState.IsOnGround)
        {
            switch (session.Phase)
            {
                case FlightPhase.Preflight or FlightPhase.IFRClearanceIssued or FlightPhase.Pushback:
                    if (session.Phase == FlightPhase.Preflight
                        && session.IfrClearance is null
                        && !vfr
                        && Mentions(transcript, "clearance"))
                    {
                        return (PilotIntentType.RequestIFRClearance, 0.65);
                    }

                    if (Mentions(transcript, "pushback", "push back", "push and"))
                    {
                        return (PilotIntentType.RequestPushback, 0.65);
                    }

                    if (Mentions(transcript, "taxi")
                        || (vfr && session.TaxiClearance is null
                            && Mentions(transcript, "depart", "vfr", "local", "circuit", "start", "ready")))
                    {
                        return (PilotIntentType.RequestTaxi, 0.65);
                    }

                    break;

                case FlightPhase.Landed or FlightPhase.TaxiIn:
                    if (Mentions(transcript, "stand", "parking", "gate"))
                    {
                        return (PilotIntentType.RequestTaxiToStand, 0.65);
                    }

                    if (Mentions(transcript, "vacated", "clear of", "runway is clear"))
                    {
                        return (PilotIntentType.ReportRunwayVacated, 0.65);
                    }

                    break;

                case FlightPhase.TaxiOut or FlightPhase.HoldingShort or FlightPhase.LineUp:
                    if (Mentions(transcript, "ready", "takeoff", "take off", "line up")
                        || (vfr && Mentions(transcript, "depart")))
                    {
                        return (PilotIntentType.ReadyForDeparture, 0.65);
                    }

                    if (Mentions(transcript, "holding", "hold"))
                    {
                        return (PilotIntentType.ReportHoldingShort, 0.65);
                    }

                    break;
            }

            return (PilotIntentType.Unknown, 0);
        }

        // In the air, outside the circuit (whose legs have rules of their own).
        if (session.Phase is FlightPhase.InitialClimb or FlightPhase.Departure or FlightPhase.Enroute
            or FlightPhase.Descent or FlightPhase.Arrival)
        {
            if (Mentions(transcript, "request")
                && Mentions(transcript, "flight level", "altitude")
                && Altitude.Parse(transcript) is Altitude requested)
            {
                return requested.Feet > session.AircraftState.IndicatedAltitudeFeet
                    ? (PilotIntentType.RequestClimb, 0.65)
                    : (PilotIntentType.RequestDescent, 0.65);
            }

            if (Mentions(transcript, "hello", "flight level", "altitude", "passing", "climbing", "descending", "feet"))
            {
                return (PilotIntentType.CheckIn, 0.65);
            }
        }

        return (PilotIntentType.Unknown, 0);
    }

    private static bool IsBriefAcknowledgement(string transcript)
    {
        string[] tokens = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 6)
        {
            return false;
        }

        return transcript.Contains("roger", StringComparison.Ordinal)
            || transcript.Contains("will comply", StringComparison.Ordinal)
            || transcript.Contains("thank you", StringComparison.Ordinal)
            || transcript.Contains("thanks", StringComparison.Ordinal)
            || transcript.Contains("got it", StringComparison.Ordinal)
            || transcript.Contains("stand by", StringComparison.Ordinal)
            || transcript.Contains("go ahead", StringComparison.Ordinal);
    }

    /// <summary>
    /// Uses the session state to sharpen an ambiguous match.
    /// </summary>
    /// <remarks>
    /// This is where "seven thousand, squawk four two seven one" — which matches nothing
    /// strongly on its own — becomes a high-confidence clearance readback, because the session
    /// is holding a pending clearance readback expecting exactly those items.
    /// </remarks>
    private static (PilotIntentType, double) RefineWithSessionContext(
        PilotIntentType type,
        double confidence,
        AtcSession session,
        IntentEntities entities,
        string transcript)
    {
        if (session.PendingReadback is not PendingReadback pending)
        {
            return (type, confidence);
        }

        // Which intent type would a readback of this pending clearance be?
        PilotIntentType expectedReadbackType = pending.ClearanceType switch
        {
            ClearanceType.IfrClearance => PilotIntentType.ClearanceReadback,
            ClearanceType.Taxi or ClearanceType.TaxiToStand or ClearanceType.RunwayCrossing
                => PilotIntentType.TaxiReadback,
            ClearanceType.Takeoff or ClearanceType.LineUp => PilotIntentType.TakeoffReadback,
            ClearanceType.LandingClearance => PilotIntentType.LandingReadback,
            ClearanceType.ApproachClearance => PilotIntentType.LandingReadback,
            ClearanceType.Climb or ClearanceType.Descent => PilotIntentType.AltitudeReadback,
            ClearanceType.Heading => PilotIntentType.HeadingReadback,
            _ => PilotIntentType.Unknown,
        };

        if (expectedReadbackType == PilotIntentType.Unknown)
        {
            return (type, confidence);
        }

        // Count how many of the expected items actually appear in this transmission.
        int matchedItems = CountMatchingExpectedItems(
            pending,
            entities,
            transcript,
            session.Callsign);

        if (matchedItems == 0)
        {
            return (type, confidence);
        }

        double coverage = (double)matchedItems / Math.Max(pending.ExpectedItems.Count, 1);

        // Already classified as the right kind of readback — just raise confidence.
        if (type == expectedReadbackType)
        {
            return (type, Math.Min(1.0, confidence + (coverage * 0.2)));
        }

        // A taxi readback may naturally begin with "holding short", which the phrase-only
        // rules classify as a position report with high confidence. While a taxi clearance
        // is awaiting readback, route/runway values that match the outstanding clearance
        // are stronger evidence than that opening phrase. Reclassifying here lets the
        // readback evaluator accept the complete clearance or name only a genuinely
        // missing item (for example, "Alfa" when "Alfa six" was assigned).
        bool contextCanResolveIntent =
            type == PilotIntentType.Unknown
            || confidence < 0.8
            || (expectedReadbackType == PilotIntentType.TaxiReadback
                && type == PilotIntentType.ReportHoldingShort);

        if (contextCanResolveIntent && coverage >= 0.5)
        {
            return (expectedReadbackType, Math.Min(0.95, 0.65 + (coverage * 0.3)));
        }

        return (type, confidence);
    }

    private static int CountMatchingExpectedItems(
        PendingReadback pending,
        IntentEntities entities,
        string transcript,
        Callsign? sessionCallsign)
    {
        int matched = 0;

        foreach (ExpectedReadbackItem expected in pending.ExpectedItems)
        {
            bool present = expected.Type switch
            {
                ReadbackItemType.Runway or ReadbackItemType.HoldShortRunway => entities.Runway is not null,
                ReadbackItemType.InitialAltitude or ReadbackItemType.CruiseAltitude => entities.Altitude is not null,
                ReadbackItemType.Squawk => entities.Squawk is not null,
                ReadbackItemType.Frequency => entities.Frequency is not null,
                ReadbackItemType.Heading => entities.Heading is not null,
                ReadbackItemType.TaxiRoute => entities.Taxiways.Count > 0,
                ReadbackItemType.Callsign =>
                    sessionCallsign?.MatchesTranscript(transcript) == true,
                _ => false,
            };

            if (present)
            {
                matched++;
            }
        }

        return matched;
    }

    /// <summary>Recognises the session callsign in the transmission.</summary>
    private static Callsign? ExtractCallsign(string transcript, AtcSession session)
    {
        if (session.Callsign is Callsign expected && expected.MatchesTranscript(transcript))
        {
            return expected;
        }

        return null;
    }

    /// <summary>Pulls every aviation value the transcript contains.</summary>
    private static IntentEntities ExtractEntities(string transcript)
    {
        bool mentionsHoldShort =
            transcript.Contains("hold short", StringComparison.Ordinal)
            || transcript.Contains("holding short", StringComparison.Ordinal);

        // "left hand" and "right hand" name the direction of the circuit, not a runway side: left
        // in the text would otherwise turn "downwind left hand runway 08" into runway 08L.
        RunwayIdentifier? runway = RunwayIdentifier.Parse(CircuitHand().Replace(transcript, " "));

        return new IntentEntities
        {
            Runway = runway,
            Altitude = Altitude.Parse(transcript),
            Squawk = SquawkCode.TryParse(transcript, out SquawkCode? squawk) ? squawk : null,
            Frequency = Frequency.Parse(transcript),
            Heading = ExtractHeading(transcript),
            Taxiways = ExtractTaxiways(transcript),
            Waypoint = ExtractWaypoint(transcript),
            ApproachName = ExtractApproachName(transcript),
            Stand = ExtractStand(transcript),
            PushbackDirection = ExtractPushbackDirection(transcript),
            MentionsHoldShort = mentionsHoldShort,
            HoldShortRunway = mentionsHoldShort ? runway : null,
        };
    }

    /// <summary>Extracts an explicitly spoken relative tail direction for pushback.</summary>
    private static string? ExtractPushbackDirection(string transcript)
    {
        bool mentionsPushback =
            transcript.Contains("pushback", StringComparison.Ordinal)
            || transcript.Contains("push back", StringComparison.Ordinal)
            || transcript.Contains("push and start", StringComparison.Ordinal);
        if (!mentionsPushback)
        {
            return null;
        }

        if (transcript.Contains("tail to the left", StringComparison.Ordinal)
            || transcript.Contains("tail left", StringComparison.Ordinal))
        {
            return PushbackDirections.TailLeft;
        }

        if (transcript.Contains("tail to the right", StringComparison.Ordinal)
            || transcript.Contains("tail right", StringComparison.Ordinal))
        {
            return PushbackDirections.TailRight;
        }

        string[] anchors = ["pushback ", "push back ", "push and start ", "facing ", "face "];
        foreach (string anchor in anchors)
        {
            int index = transcript.IndexOf(anchor, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string remainder = transcript[(index + anchor.Length)..];
            string candidate = string.Join(
                ' ',
                remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
            string? direction = PushbackDirections.Normalize(candidate);
            if (direction is not null)
            {
                return direction;
            }

            direction = PushbackDirections.Normalize(
                remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            if (direction is not null)
            {
                return direction;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a heading, but only when the word "heading" is present.
    /// </summary>
    /// <remarks>
    /// Without that anchor any three-digit run would be read as a heading, so "squawk four two
    /// seven one" would produce a spurious heading of 427 alongside the correct squawk.
    /// </remarks>
    private static Heading? ExtractHeading(string transcript)
    {
        int index = transcript.IndexOf("heading", StringComparison.Ordinal);
        return index < 0 ? null : Heading.Parse(transcript[(index + "heading".Length)..]);
    }

    /// <summary>Extracts taxiway identifiers following the word "via".</summary>
    private static IReadOnlyList<string> ExtractTaxiways(string transcript)
    {
        int viaIndex = transcript.IndexOf("via", StringComparison.Ordinal);
        if (viaIndex < 0)
        {
            return [];
        }

        // Stop at "hold short" so the runway designator is not read as a taxiway.
        string scope = transcript[(viaIndex + 3)..];
        int stopIndex = scope.IndexOf("hold", StringComparison.Ordinal);
        if (stopIndex >= 0)
        {
            scope = scope[..stopIndex];
        }

        return PhoneticAlphabet.ExtractTaxiwayIdentifiers(scope);
    }

    /// <summary>Extracts a waypoint name following "direct".</summary>
    private static string? ExtractWaypoint(string transcript)
    {
        int index = transcript.IndexOf("direct", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        string remainder = transcript[(index + "direct".Length)..].Trim();
        if (remainder.StartsWith("to ", StringComparison.Ordinal))
        {
            remainder = remainder[3..].Trim();
        }

        string[] tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        // A spelled-out waypoint ("kilo india lima oscar") reassembles into letters; a spoken
        // one ("KILO") is taken as the single token.
        string letters = PhoneticAlphabet.ExtractLetters(remainder);
        if (letters.Length >= 3)
        {
            return letters;
        }

        return tokens[0].ToUpperInvariant();
    }

    /// <summary>Extracts the approach type mentioned.</summary>
    private static string? ExtractApproachName(string transcript)
    {
        if (transcript.Contains("ils", StringComparison.Ordinal))
        {
            return "ILS";
        }

        if (transcript.Contains("rnav", StringComparison.Ordinal)
            || transcript.Contains("r nav", StringComparison.Ordinal))
        {
            return "RNAV";
        }

        if (transcript.Contains("visual", StringComparison.Ordinal))
        {
            return "Visual";
        }

        if (transcript.Contains("vor", StringComparison.Ordinal))
        {
            return "VOR";
        }

        return null;
    }

    /// <summary>Extracts a stand name following "stand" or "gate".</summary>
    private static string? ExtractStand(string transcript)
    {
        foreach (string keyword in (string[])["stand", "gate"])
        {
            int index = transcript.IndexOf(keyword, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string remainder = transcript[(index + keyword.Length)..].Trim();
            string[] tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            // "stand alpha one" -> A1; "stand a1" -> A1.
            var builder = new System.Text.StringBuilder();
            foreach (string token in tokens.Take(2))
            {
                if (PhoneticAlphabet.TryGetLetter(token, out char letter))
                {
                    builder.Append(letter);
                }
                else if (token.Length <= 3 && token.All(char.IsLetterOrDigit))
                {
                    builder.Append(token.ToUpperInvariant());
                }
                else
                {
                    break;
                }
            }

            string result = builder.ToString();
            if (result.Length is >= 2 and <= 4)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>Turns extracted entities into readback items for comparison.</summary>
    private static List<ParsedReadbackItem> BuildReadbackItems(
        IntentEntities entities,
        string transcript)
    {
        var items = new List<ParsedReadbackItem>();

        if (entities.Runway is RunwayIdentifier runway)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = entities.MentionsHoldShort ? ReadbackItemType.HoldShortRunway : ReadbackItemType.Runway,
                NormalizedValue = runway.ToComparisonValue(),
                SourceText = transcript,
            });

            // A transmission such as "taxi via alpha, hold short runway one eight" carries the
            // same runway in both roles; emit both so either expectation can match.
            if (entities.MentionsHoldShort)
            {
                items.Add(new ParsedReadbackItem
                {
                    Type = ReadbackItemType.Runway,
                    NormalizedValue = runway.ToComparisonValue(),
                    SourceText = transcript,
                });
            }
        }

        if (entities.Altitude is Altitude altitude)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.InitialAltitude,
                NormalizedValue = altitude.ToComparisonValue(),
                SourceText = transcript,
            });

            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.CruiseAltitude,
                NormalizedValue = altitude.ToComparisonValue(),
                SourceText = transcript,
            });
        }

        if (entities.Squawk is SquawkCode squawk)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.Squawk,
                NormalizedValue = squawk.ToComparisonValue(),
                SourceText = transcript,
            });
        }

        if (entities.Frequency is Frequency frequency)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.Frequency,
                NormalizedValue = frequency.ToComparisonValue(),
                SourceText = transcript,
            });
        }

        if (entities.Heading is Heading heading)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.Heading,
                NormalizedValue = heading.ToComparisonValue(),
                SourceText = transcript,
            });
        }

        if (entities.Taxiways.Count > 0)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.TaxiRoute,
                NormalizedValue = string.Join(",", entities.Taxiways),
                SourceText = transcript,
            });
        }

        if (entities.ApproachName is { Length: > 0 } approach)
        {
            items.Add(new ParsedReadbackItem
            {
                Type = ReadbackItemType.ApproachName,
                NormalizedValue = approach,
                SourceText = transcript,
            });
        }

        return items;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(left|right) ?hand\b")]
    private static partial System.Text.RegularExpressions.Regex CircuitHand();

    /// <summary>One phrase-matching rule.</summary>
    private sealed record IntentRule(PilotIntentType Type, double Confidence, string[] Phrases);
}
