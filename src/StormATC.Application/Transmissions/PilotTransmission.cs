using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Transmissions;

/// <summary>
/// One transmission from the pilot, in the form the pipeline accepts it.
/// </summary>
/// <remarks>
/// The <see cref="TransmissionId"/> is what makes the pipeline idempotent. A push-to-talk
/// release that fires twice, or a retry after a UI hiccup, produces the same identifier and is
/// processed once — which matters because processing twice would issue two clearances.
/// </remarks>
public sealed record PilotTransmission
{
    /// <summary>Unique identifier. Re-submitting the same identifier is a no-op.</summary>
    public required Guid TransmissionId { get; init; }

    /// <summary>The session this belongs to.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>When the pilot finished speaking.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier tying every log line and diagnostic for this transmission together.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Where the transmission came from.</summary>
    public required TransmissionSource Source { get; init; }

    /// <summary>The transcript exactly as received.</summary>
    public required string RawTranscript { get; init; }

    /// <summary>Speech-to-text confidence, where the provider reported it.</summary>
    public double? SpeechConfidence { get; init; }

    /// <summary>Creates a transmission with generated identifiers.</summary>
    public static PilotTransmission Create(
        Guid sessionId,
        string rawTranscript,
        TransmissionSource source,
        DateTimeOffset timestamp,
        double? speechConfidence = null)
    {
        Guid id = Guid.CreateVersion7();

        return new PilotTransmission
        {
            TransmissionId = id,
            SessionId = sessionId,
            Timestamp = timestamp,
            CorrelationId = id.ToString("N")[..12],
            Source = source,
            RawTranscript = rawTranscript,
            SpeechConfidence = speechConfidence,
        };
    }
}

/// <summary>The outcome of processing one pilot transmission.</summary>
public sealed record TransmissionProcessingResult
{
    /// <summary>Whether the transmission was processed at all.</summary>
    public required bool Processed { get; init; }

    /// <summary>True when this transmission had already been processed and was ignored.</summary>
    public bool WasDuplicate { get; init; }

    /// <summary>True when the selected transmit radio was not on the controller frequency.</summary>
    public bool WasIgnoredOffFrequency { get; init; }

    /// <summary>True when the pilot attempted to transmit while monitoring receive-only ATIS.</summary>
    public bool WasIgnoredOnAtis { get; init; }

    /// <summary>The frequency StormATC expected for a radio-gated transmission.</summary>
    public Frequency? ExpectedFrequency { get; init; }

    /// <summary>The aircraft's selected transmit frequency when the transmission was gated.</summary>
    public Frequency? TunedFrequency { get; init; }

    /// <summary>The session after any approved state changes were applied.</summary>
    public AtcSession? UpdatedSession { get; init; }

    /// <summary>What the parser made of the transmission.</summary>
    public PilotIntent? Intent { get; init; }

    /// <summary>What the engine decided.</summary>
    public AtcDecision? Decision { get; init; }

    /// <summary>The readback verdict, when one applied.</summary>
    public ReadbackAssessment? ReadbackAssessment { get; init; }

    /// <summary>The controller's spoken response.</summary>
    public string? ControllerSpokenText { get; init; }

    /// <summary>The controller's on-screen response.</summary>
    public string? ControllerDisplayText { get; init; }

    /// <summary>True when the response came from deterministic phraseology.</summary>
    public bool UsedDeterministicFallback { get; init; }

    /// <summary>Per-stage timings, shown on the Diagnostics page.</summary>
    public TransmissionTimings Timings { get; init; } = new();

    /// <summary>A user-facing error, when processing failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The correlation identifier, for cross-referencing logs.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Builds a duplicate-suppression result.</summary>
    public static TransmissionProcessingResult Duplicate(string correlationId) => new()
    {
        Processed = false,
        WasDuplicate = true,
        CorrelationId = correlationId,
    };

    /// <summary>Builds a failure result.</summary>
    public static TransmissionProcessingResult Failed(string correlationId, string error) => new()
    {
        Processed = false,
        CorrelationId = correlationId,
        ErrorMessage = error,
    };

    /// <summary>Builds a silent result for a transmission no controller could hear.</summary>
    public static TransmissionProcessingResult IgnoredByRadio(
        string correlationId,
        AtcSession session,
        bool onAtis) => new()
    {
        Processed = false,
        WasIgnoredOffFrequency = !onAtis,
        WasIgnoredOnAtis = onAtis,
        ExpectedFrequency = session.CurrentFrequency,
        TunedFrequency = session.AircraftState.Radio.ActiveTransmitFrequency,
        UpdatedSession = session,
        CorrelationId = correlationId,
    };
}

/// <summary>Per-stage timings for one transmission.</summary>
public sealed record TransmissionTimings
{
    /// <summary>Transcript normalisation.</summary>
    public TimeSpan Normalization { get; init; }

    /// <summary>Intent parsing, including any language model call.</summary>
    public TimeSpan IntentParsing { get; init; }

    /// <summary>ATC engine evaluation.</summary>
    public TimeSpan EngineEvaluation { get; init; }

    /// <summary>Phraseology rendering, including any language model call.</summary>
    public TimeSpan PhraseologyRendering { get; init; }

    /// <summary>Persistence.</summary>
    public TimeSpan Persistence { get; init; }

    /// <summary>End-to-end time from receiving the transcript to publishing the response.</summary>
    public TimeSpan Total { get; init; }
}
