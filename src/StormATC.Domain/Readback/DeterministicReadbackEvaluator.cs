using System.Text;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Readback;

/// <summary>
/// The production readback evaluator. Entirely local, entirely deterministic.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator works in two passes. First it uses whatever structured values the intent
/// parser already extracted, which is the reliable path. Then, for any required item the
/// parser did not produce, it re-scans the raw transcript for that specific item type — a
/// targeted second look that recovers readbacks the general parser missed, without the risk
/// of a general re-parse inventing values.
/// </para>
/// <para>
/// Everything compares normalised values. "seven thousand" and "7000" are the same altitude;
/// "one two one decimal seven" and "121.700" are the same frequency; "zero one left" and "01L"
/// are the same runway. That normalisation lives in the value objects, so this class only ever
/// compares two already-canonical strings.
/// </para>
/// </remarks>
public sealed class DeterministicReadbackEvaluator : IReadbackEvaluator
{
    /// <inheritdoc />
    public ReadbackAssessment Evaluate(
        PendingReadback pending,
        PilotIntent intent,
        string transcript,
        AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(session);

        CallsignStatus callsignStatus = EvaluateCallsign(intent, transcript, session);

        // A transmission using another aircraft's callsign is not this aircraft's readback,
        // whatever else it contains.
        if (callsignStatus == CallsignStatus.Incorrect)
        {
            return new ReadbackAssessment
            {
                Outcome = ReadbackOutcome.WrongCallsign,
                CallsignStatus = callsignStatus,
                Confidence = 0.9,
                MissingItems = [.. pending.ExpectedItems.Select(item => item.Type)],
                RequiredCorrection = BuildWrongCallsignCorrection(session),
            };
        }

        var correct = new List<ReadbackItemType>();
        var missing = new List<ReadbackItemType>();
        var incorrect = new List<IncorrectReadbackItem>();
        var previouslySatisfied = new HashSet<ReadbackItemType>(pending.SatisfiedItems);

        // Index what the parser found, so each expected item can be looked up directly.
        Dictionary<ReadbackItemType, ParsedReadbackItem> parsed = [];
        foreach (ParsedReadbackItem item in intent.ReadbackItems)
        {
            parsed.TryAdd(item.Type, item);
        }

        foreach (ExpectedReadbackItem expected in pending.ExpectedItems)
        {
            if (expected.Type == ReadbackItemType.Callsign)
            {
                // Handled separately above — the callsign is not an ordinary value comparison.
                if (callsignStatus == CallsignStatus.Correct)
                {
                    correct.Add(ReadbackItemType.Callsign);
                }
                else if (previouslySatisfied.Contains(ReadbackItemType.Callsign))
                {
                    // The controller asked only for a correction to another item. The callsign
                    // from the earlier partial readback remains valid when it is not repeated.
                    correct.Add(ReadbackItemType.Callsign);
                }
                else
                {
                    missing.Add(ReadbackItemType.Callsign);
                }

                continue;
            }

            string? actual = parsed.TryGetValue(expected.Type, out ParsedReadbackItem? item)
                ? item.NormalizedValue
                : ExtractFromTranscript(expected.Type, transcript);

            if (actual is null)
            {
                if (previouslySatisfied.Contains(expected.Type))
                {
                    correct.Add(expected.Type);
                }
                else
                {
                    missing.Add(expected.Type);
                }

                continue;
            }

            if (ValuesMatch(expected.Type, expected.ExpectedValue, actual))
            {
                correct.Add(expected.Type);
            }
            else
            {
                incorrect.Add(new IncorrectReadbackItem
                {
                    Type = expected.Type,
                    ExpectedValue = expected.ExpectedValue,
                    ActualValue = actual,
                    ExpectedSpokenValue = expected.SpokenValue,
                });
            }
        }

        // Anything the pilot read back that was not asked for.
        var unexpected = new List<ReadbackItemType>();
        foreach (ParsedReadbackItem item in intent.ReadbackItems)
        {
            bool wasExpected = pending.ExpectedItems.Any(e => e.Type == item.Type);
            if (!wasExpected)
            {
                unexpected.Add(item.Type);
            }
        }

        // Only critical items decide the outcome. A missing route summary is a nicety; a
        // missing hold-short instruction is not.
        bool hasCriticalMissing = missing.Any(type => IsCritical(pending, type));
        bool hasCriticalIncorrect = incorrect.Any(item => IsCritical(pending, item.Type));

        ReadbackOutcome outcome = (hasCriticalIncorrect, hasCriticalMissing) switch
        {
            (true, _) => ReadbackOutcome.Incorrect,
            (false, true) => ReadbackOutcome.Incomplete,
            _ => ReadbackOutcome.Correct,
        };

        return new ReadbackAssessment
        {
            Outcome = outcome,
            CorrectItems = correct,
            MissingItems = missing,
            IncorrectItems = incorrect,
            UnexpectedItems = unexpected,
            CallsignStatus = callsignStatus,
            Confidence = CalculateConfidence(intent, correct.Count, missing.Count, incorrect.Count),
            RequiredCorrection = outcome == ReadbackOutcome.Correct
                ? null
                : BuildCorrection(session, pending, missing, incorrect),
        };
    }

    /// <summary>
    /// Decides whether the transmission addressed this aircraft.
    /// </summary>
    private static CallsignStatus EvaluateCallsign(
        PilotIntent intent,
        string transcript,
        AtcSession session)
    {
        if (session.Callsign is not Callsign expected)
        {
            // No flight plan loaded — cannot judge the callsign, so do not penalise it.
            return CallsignStatus.Correct;
        }

        if (intent.Callsign is Callsign spoken)
        {
            return spoken == expected ? CallsignStatus.Correct : CallsignStatus.Incorrect;
        }

        if (expected.MatchesTranscript(transcript))
        {
            return CallsignStatus.Correct;
        }

        // Distinguish "said nothing" from "said someone else's callsign". A transmission with
        // a recognisable callsign-shaped token that is not ours is a wrong-callsign event; one
        // with no callsign at all is merely incomplete.
        return ContainsForeignCallsign(transcript, expected)
            ? CallsignStatus.Incorrect
            : CallsignStatus.Missing;
    }

    /// <summary>
    /// Looks for a callsign-shaped token belonging to another aircraft.
    /// </summary>
    /// <remarks>
    /// The heuristic is a telephony-style word followed by a digit run, where the digit run
    /// differs from ours. It is deliberately conservative: a false "wrong callsign" is more
    /// disruptive than a missed one, and the engine treats <see cref="CallsignStatus.Missing"/>
    /// as a recoverable prompt rather than a rejection.
    /// </remarks>
    private static bool ContainsForeignCallsign(string transcript, Callsign expected)
    {
        if (expected.Telephony is not { Length: > 0 } telephony)
        {
            return false;
        }

        string normalized = Callsign.Normalize(transcript);

        int index = normalized.IndexOf(telephony, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        // Our telephony is present but the full callsign did not match — read the digits that
        // follow and see whether they are a different flight number.
        int digitStart = index + telephony.Length;
        var digits = new StringBuilder();
        for (int i = digitStart; i < normalized.Length && char.IsDigit(normalized[i]); i++)
        {
            digits.Append(normalized[i]);
        }

        return digits.Length >= 2
            && !string.Equals(digits.ToString(), expected.NumericSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-scans the transcript for one specific item type, used when the general parser did not
    /// produce that item.
    /// </summary>
    private static string? ExtractFromTranscript(ReadbackItemType type, string transcript) => type switch
    {
        ReadbackItemType.Runway or ReadbackItemType.HoldShortRunway =>
            RunwayIdentifier.Parse(transcript)?.ToComparisonValue(),

        ReadbackItemType.InitialAltitude or ReadbackItemType.CruiseAltitude =>
            Altitude.Parse(transcript)?.ToComparisonValue(),

        ReadbackItemType.Squawk =>
            SquawkCode.TryParse(transcript, out SquawkCode? squawk) ? squawk.Value.ToComparisonValue() : null,

        ReadbackItemType.Frequency =>
            Frequency.Parse(transcript)?.ToComparisonValue(),

        ReadbackItemType.Heading =>
            Heading.Parse(transcript)?.ToComparisonValue(),

        ReadbackItemType.TaxiRoute =>
            ExtractTaxiRoute(transcript),

        _ => null,
    };

    /// <summary>
    /// Pulls the taxiway letters out of a transcript. "via alpha and charlie" yields "A,C".
    /// </summary>
    private static string? ExtractTaxiRoute(string transcript)
    {
        // Only look after "via" when present, so that "hold short of runway one eight via
        // alpha" does not pick up stray letters from earlier in the transmission.
        string scope = transcript;
        int viaIndex = transcript.IndexOf("via", StringComparison.OrdinalIgnoreCase);
        if (viaIndex >= 0)
        {
            scope = transcript[(viaIndex + 3)..];
        }

        string letters = PhoneticAlphabet.ExtractLetters(scope);
        if (letters.Length == 0)
        {
            return null;
        }

        return string.Join(",", letters.Select(c => c.ToString()));
    }

    /// <summary>
    /// Compares an expected and an actual value for one item type.
    /// </summary>
    private static bool ValuesMatch(ReadbackItemType type, string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return type switch
        {
            // Taxi route: order matters, but the pilot may name only the taxiways and skip
            // separators. Compare the letter sequences.
            ReadbackItemType.TaxiRoute => NormalizeRoute(expected) == NormalizeRoute(actual),

            // Runways: compare through the value object so "1L" and "01L" match.
            ReadbackItemType.Runway or ReadbackItemType.HoldShortRunway =>
                RunwayIdentifier.TryCreate(expected, out RunwayIdentifier a)
                && RunwayIdentifier.TryCreate(actual, out RunwayIdentifier b)
                && a == b,

            // Free-text items compare case- and whitespace-insensitively.
            ReadbackItemType.Route or ReadbackItemType.DepartureProcedure
                or ReadbackItemType.ApproachName =>
                string.Equals(Compact(expected), Compact(actual), StringComparison.OrdinalIgnoreCase),

            _ => false,
        };

        static string NormalizeRoute(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        static string Compact(string value) =>
            new(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private static bool IsCritical(PendingReadback pending, ReadbackItemType type)
    {
        foreach (ExpectedReadbackItem item in pending.ExpectedItems)
        {
            if (item.Type == type)
            {
                return item.IsCritical;
            }
        }

        return false;
    }

    private static double CalculateConfidence(
        PilotIntent intent,
        int correctCount,
        int missingCount,
        int incorrectCount)
    {
        int total = correctCount + missingCount + incorrectCount;
        if (total == 0)
        {
            return 0.5;
        }

        // Start from how sure the parser was, then weight by how much of the readback resolved
        // cleanly. A transcript where most items were found is one we can judge confidently
        // even if the verdict is "wrong".
        double resolvedFraction = (double)(correctCount + incorrectCount) / total;
        return Math.Clamp((intent.Confidence * 0.4) + (resolvedFraction * 0.6), 0, 1);
    }

    /// <summary>
    /// Builds a targeted correction naming only what was wrong or missing.
    /// </summary>
    private static string BuildCorrection(
        AtcSession session,
        PendingReadback pending,
        List<ReadbackItemType> missing,
        List<IncorrectReadbackItem> incorrect)
    {
        string callsign = session.Callsign?.ToSpoken() ?? "Aircraft";
        var parts = new List<string>();

        // Wrong values first — those are the ones that would cause a deviation.
        foreach (IncorrectReadbackItem item in incorrect)
        {
            if (IsCritical(pending, item.Type))
            {
                parts.Add(item.ExpectedSpokenValue);
            }
        }

        foreach (ReadbackItemType type in missing)
        {
            if (!IsCritical(pending, type))
            {
                continue;
            }

            ExpectedReadbackItem? expected = pending.ExpectedItems.FirstOrDefault(e => e.Type == type);
            if (expected is not null)
            {
                parts.Add(expected.SpokenValue);
            }
        }

        if (parts.Count == 0)
        {
            return $"{callsign}, say again your readback.";
        }

        // "negative" for a wrong value, "readback correct except" for an omission — the
        // distinction real controllers make.
        string lead = incorrect.Count > 0 ? "negative" : "readback incomplete";
        return $"{callsign}, {lead}, {string.Join(", ", parts)}.";
    }

    private static string BuildWrongCallsignCorrection(AtcSession session)
    {
        string callsign = session.Callsign?.ToSpoken() ?? "Aircraft";
        return $"{callsign}, verify callsign, say again.";
    }
}
