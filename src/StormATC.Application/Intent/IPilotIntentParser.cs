using StormATC.Domain;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Intent;

/// <summary>
/// Turns a normalised transcript into a structured intent hypothesis.
/// </summary>
public interface IPilotIntentParser
{
    /// <summary>A stable identifier for this parser, recorded in diagnostics.</summary>
    string ParserName { get; }

    /// <summary>Parses a transcript in the context of the current session.</summary>
    Task<PilotIntent> ParseAsync(
        string normalizedTranscript,
        AtcSession session,
        CancellationToken cancellationToken);
}

/// <summary>
/// Escalates from the local parser to the language model only when the local result is not
/// good enough.
/// </summary>
/// <remarks>
/// <para>
/// The escalation policy is the point of this class. Calling a model on every transmission
/// would add latency and cost to the 90% of transmissions that are standard phraseology, and
/// would put a non-deterministic component on the critical path for readbacks. So the model
/// is invoked only when the deterministic parser is genuinely unsure.
/// </para>
/// <para>
/// If the model is unavailable or its answer fails validation, the deterministic result stands
/// — degraded, flagged, but never replaced by a guess.
/// </para>
/// </remarks>
public sealed class EscalatingPilotIntentParser(
    DeterministicPilotIntentParser deterministicParser,
    ILanguageModelIntentParser? languageModelParser,
    double escalationThreshold = 0.7) : IPilotIntentParser
{
    private readonly DeterministicPilotIntentParser _deterministicParser =
        deterministicParser ?? throw new ArgumentNullException(nameof(deterministicParser));

    /// <inheritdoc />
    public string ParserName => "Escalating";

    /// <inheritdoc />
    public async Task<PilotIntent> ParseAsync(
        string normalizedTranscript,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        PilotIntent deterministic = await _deterministicParser
            .ParseAsync(normalizedTranscript, session, cancellationToken)
            .ConfigureAwait(false);

        if (!ShouldEscalate(deterministic))
        {
            return deterministic;
        }

        if (languageModelParser is null || !languageModelParser.IsAvailable)
        {
            // No model configured. Return the local result as-is: low confidence here means
            // the engine will ask the pilot to repeat, which is the correct degraded behaviour.
            return deterministic;
        }

        PilotIntent? modelIntent = await languageModelParser
            .ParseAsync(normalizedTranscript, session, deterministic, cancellationToken)
            .ConfigureAwait(false);

        if (modelIntent is null)
        {
            // The model failed or its output was rejected by validation. Keep the local result
            // and record where it came from, so diagnostics show the rejection happened.
            return deterministic with
            {
                Source = IntentParserSource.DeterministicAfterModelRejection,
            };
        }

        // Only accept the model's answer if it is actually more confident than the local one.
        return modelIntent.Confidence > deterministic.Confidence ? modelIntent : deterministic;
    }

    /// <summary>Decides whether the local result warrants a model call.</summary>
    private bool ShouldEscalate(PilotIntent deterministic)
    {
        if (deterministic.Type == PilotIntentType.Unknown)
        {
            return true;
        }

        if (deterministic.Confidence < escalationThreshold)
        {
            return true;
        }

        // A safety-critical intent gets a second opinion at a higher bar than an ordinary one:
        // being wrong about "ready for departure" costs more than being wrong about a greeting.
        return deterministic.IsSafetyCritical && deterministic.Confidence < escalationThreshold + 0.15;
    }
}

/// <summary>
/// The language-model-backed intent parser.
/// </summary>
/// <remarks>
/// Returns null rather than throwing on any failure — provider outage, malformed JSON, schema
/// violation, or a value that fails domain validation. The caller treats null as "keep the
/// deterministic result", which keeps the failure path simple and total.
/// </remarks>
public interface ILanguageModelIntentParser
{
    /// <summary>True when a model is configured and reachable.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Asks the model to interpret a transcript the local parser could not.
    /// </summary>
    /// <param name="normalizedTranscript">The cleaned transcript.</param>
    /// <param name="session">Current session, for callsign and pending-readback context.</param>
    /// <param name="deterministicHypothesis">
    /// What the local parser thought, supplied to the model as a starting point.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A validated intent, or null when nothing usable could be obtained.</returns>
    Task<PilotIntent?> ParseAsync(
        string normalizedTranscript,
        AtcSession session,
        PilotIntent deterministicHypothesis,
        CancellationToken cancellationToken);
}
