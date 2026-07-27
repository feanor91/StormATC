using System.Text;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// Builds the recognition context sent to Deepgram and applies explicit user-trained corrections.
/// </summary>
internal static class DeepgramRecognitionProfile
{
    private const int MaximumKeyterms = 100;
    private const int MaximumKeytermTokens = 500;

    private static readonly string[] AviationKeyterms =
    [
        "IFR clearance",
        "pushback",
        "ready for taxi",
        "holding short",
        "ready for departure",
        "cleared for takeoff",
        "request climb",
        "request descent",
        "flight level",
        "squawk",
        "runway",
        "taxiway",
        "localizer",
        "request approach",
        "established",
        "cleared to land",
        "runway vacated",
        "taxi to stand",
        "go around",
        "mayday",
        "pan pan",
        "say again",
        "Delivery",
        "Ground",
        "Tower",
        "Departure",
        "Approach",
        "Center",
        "contact Ground",
        "remain this frequency",
        "request pushback and start",
        "what is the frequency",
    ];

    internal static SpeechRecognitionProfile FromOptions(DeepgramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new SpeechRecognitionProfile
        {
            InputDeviceName = NullIfWhiteSpace(options.InputDeviceName),
            Language = string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language.Trim(),
            Keyterms = options.RecognitionKeyterms
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(term => term.Trim())
                .ToArray(),
            TranscriptCorrections = new Dictionary<string, string>(
                options.TranscriptCorrections
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                        && !string.IsNullOrWhiteSpace(pair.Value)),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Combines the personal profile with the current flight plan and common ATC vocabulary.
    /// </summary>
    internal static IReadOnlyList<string> BuildKeyterms(
        SpeechRecognitionProfile profile,
        AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tokenCount = 0;

        void Add(string? value)
        {
            string? term = NullIfWhiteSpace(value);
            if (term is null || seen.Contains(term) || terms.Count >= MaximumKeyterms)
            {
                return;
            }

            int tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (tokenCount + tokens > MaximumKeytermTokens)
            {
                return;
            }

            seen.Add(term);
            terms.Add(term);
            tokenCount += tokens;
        }

        foreach (string term in profile.Keyterms)
        {
            Add(term);
        }

        if (session.Callsign is { } callsign)
        {
            Add(callsign.Normalized);
            Add(callsign.ToSpoken());
        }

        if (session.FlightPlan is { } plan)
        {
            Add(plan.DepartureIcao);
            Add(PhoneticAlphabet.SpellOut(plan.DepartureIcao));
            Add(plan.DestinationIcao);
            Add(PhoneticAlphabet.SpellOut(plan.DestinationIcao));
            Add(plan.StandardInstrumentDeparture);
            Add(plan.StandardTerminalArrival);
            Add(plan.Approach);
            Add(plan.DepartureRunway?.ToSpoken());
            Add(plan.ArrivalRunway?.ToSpoken());

            foreach (string waypoint in plan.RouteLegs
                .Select(leg => leg.Identifier)
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .Take(20))
            {
                Add(waypoint);
            }
        }

        // Bias recognition toward the exact values in the live clearance. This updates for
        // every pending readback and helps preserve easily confused digit sequences.
        if (session.PendingReadback is { } pendingReadback)
        {
            foreach (ExpectedReadbackItem expected in pendingReadback.ExpectedItems)
            {
                Add(expected.SpokenValue);
            }
        }

        foreach (string term in AviationKeyterms)
        {
            Add(term);
        }

        return terms;
    }

    /// <summary>
    /// Applies only corrections the user explicitly approved in the trainer.
    /// </summary>
    internal static string ApplyCorrections(
        string transcript,
        IReadOnlyDictionary<string, string> corrections)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(corrections);

        string corrected = transcript;
        foreach ((string heard, string expected) in corrections
            .OrderByDescending(pair => pair.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(expected))
            {
                continue;
            }

            if (Comparable(corrected) == Comparable(heard))
            {
                return expected.Trim();
            }

            corrected = corrected.Replace(
                heard.Trim(),
                expected.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        return corrected.Trim();
    }

    private static string Comparable(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
