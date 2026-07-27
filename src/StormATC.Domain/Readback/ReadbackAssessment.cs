using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;

namespace StormATC.Domain.Readback;

/// <summary>
/// Evaluates whether a pilot correctly read back an outstanding clearance.
/// </summary>
/// <remarks>
/// The comparison is deterministic and always local. A language model may help <i>extract</i>
/// candidate values from a messy transcript, but it never decides whether a readback was
/// correct: that verdict gates whether an aircraft may take off, so it belongs in code that
/// can be unit-tested exhaustively.
/// </remarks>
public interface IReadbackEvaluator
{
    /// <summary>
    /// Compares what the pilot said against what the clearance requires.
    /// </summary>
    /// <param name="pending">The outstanding readback, with expected values.</param>
    /// <param name="intent">The parsed pilot intent.</param>
    /// <param name="transcript">The raw transcript, used as a fallback extraction source.</param>
    /// <param name="session">The current session, used for callsign verification.</param>
    ReadbackAssessment Evaluate(
        PendingReadback pending,
        PilotIntent intent,
        string transcript,
        AtcSession session);
}

/// <summary>The outcome of comparing a pilot readback against the issued clearance.</summary>
public sealed record ReadbackAssessment
{
    /// <summary>Overall verdict.</summary>
    public required ReadbackOutcome Outcome { get; init; }

    /// <summary>Items read back correctly.</summary>
    public IReadOnlyList<ReadbackItemType> CorrectItems { get; init; } = [];

    /// <summary>Required items the pilot did not mention.</summary>
    public IReadOnlyList<ReadbackItemType> MissingItems { get; init; } = [];

    /// <summary>Items read back with the wrong value.</summary>
    public IReadOnlyList<IncorrectReadbackItem> IncorrectItems { get; init; } = [];

    /// <summary>
    /// Items the pilot mentioned that were not part of the clearance. Recorded for diagnostics;
    /// they do not by themselves make a readback wrong.
    /// </summary>
    public IReadOnlyList<ReadbackItemType> UnexpectedItems { get; init; } = [];

    /// <summary>Whether the pilot used the correct callsign.</summary>
    public required CallsignStatus CallsignStatus { get; init; }

    /// <summary>Confidence in this assessment, 0 to 1.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// The correction the controller should issue, or null when the readback was accepted.
    /// Corrections name only the wrong or missing items — a controller does not repeat an
    /// entire clearance because one digit of the squawk was wrong.
    /// </summary>
    public string? RequiredCorrection { get; init; }

    /// <summary>True when the clearance may now be treated as acknowledged.</summary>
    public bool IsAccepted => Outcome == ReadbackOutcome.Correct;

    /// <summary>An assessment for a transmission that was not a readback at all.</summary>
    public static ReadbackAssessment NotAReadback(CallsignStatus callsignStatus) => new()
    {
        Outcome = ReadbackOutcome.NotAReadback,
        CallsignStatus = callsignStatus,
        Confidence = 1.0,
    };
}

/// <summary>An item the pilot read back incorrectly.</summary>
public sealed record IncorrectReadbackItem
{
    /// <summary>Which item was wrong.</summary>
    public required ReadbackItemType Type { get; init; }

    /// <summary>The value the clearance actually contains.</summary>
    public required string ExpectedValue { get; init; }

    /// <summary>The value the pilot said.</summary>
    public required string ActualValue { get; init; }

    /// <summary>How the correct value should be spoken back to the pilot.</summary>
    public required string ExpectedSpokenValue { get; init; }
}

/// <summary>Whether the callsign in a transmission matched the session aircraft.</summary>
public enum CallsignStatus
{
    /// <summary>The callsign matched.</summary>
    Correct = 0,

    /// <summary>No callsign was recognised in the transmission.</summary>
    Missing,

    /// <summary>A callsign was recognised, but it belongs to a different aircraft.</summary>
    Incorrect,
}
