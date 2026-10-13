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

        // VFR circuit and arrival calls are tested before the general rules: "passing e" would
        // otherwise be read as a check-in, and "downwind" has no general rule at all.
        if (VfrIntentRules.TryMatch(transcript, session) is (PilotIntentType vfrType, double vfrConfidence))
        {
            return (vfrType, vfrConfidence);
        }

        foreach (IntentRule rule in Rules)
        {
            foreach (string phrase in rule.Phrases)
            {
                if (transcript.Contains(phrase, StringComparison.Ordinal))
                {
                    return (rule.Type, rule.Confidence);
                }
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
            || transcript.Contains("got it", StringComparison.Ordinal);
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
            ClearanceType.Taxi or ClearanceType.TaxiToStand => PilotIntentType.TaxiReadback,
            ClearanceType.Takeoff => PilotIntentType.TakeoffReadback,
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
