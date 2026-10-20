using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Intents;

/// <summary>
/// A structured interpretation of what the pilot said.
/// </summary>
/// <remarks>
/// <para>
/// An intent is a <i>hypothesis</i>, not an instruction. It may come from the fast deterministic
/// parser or from a language model, and in both cases it has been through schema and domain
/// validation before reaching the engine. The engine still decides whether the intent is
/// permitted — a valid, high-confidence <see cref="PilotIntentType.ReadyForDeparture"/> from an
/// aircraft parked at the gate is parsed correctly and refused correctly.
/// </para>
/// <para>
/// <see cref="Confidence"/> drives the escalation path: low confidence means ask the pilot to
/// repeat, never guess at a safety-critical action.
/// </para>
/// </remarks>
public sealed record PilotIntent
{
    /// <summary>What the pilot appears to want.</summary>
    public required PilotIntentType Type { get; init; }

    /// <summary>Parser confidence, 0 to 1.</summary>
    public required double Confidence { get; init; }

    /// <summary>The callsign the pilot used, when one was recognised.</summary>
    public Callsign? Callsign { get; init; }

    /// <summary>Aviation values extracted from the transmission.</summary>
    public IntentEntities Entities { get; init; } = IntentEntities.Empty;

    /// <summary>Values the pilot appears to be reading back.</summary>
    public IReadOnlyList<ParsedReadbackItem> ReadbackItems { get; init; } = [];

    /// <summary>True when the parser wants the pilot to repeat rather than acting.</summary>
    public bool RequiresClarification { get; init; }

    /// <summary>Why clarification is needed, shown to the user.</summary>
    public string? ClarificationReason { get; init; }

    /// <summary>Which parser produced this intent, for diagnostics.</summary>
    public required IntentParserSource Source { get; init; }

    /// <summary>The transcript this intent was derived from.</summary>
    public required string SourceTranscript { get; init; }

    /// <summary>An unrecognised intent, used when nothing could be parsed.</summary>
    public static PilotIntent Unknown(string transcript, IntentParserSource source) => new()
    {
        Type = PilotIntentType.Unknown,
        Confidence = 0,
        Source = source,
        SourceTranscript = transcript,
        RequiresClarification = true,
        ClarificationReason = "The transmission could not be interpreted.",
    };

    /// <summary>
    /// True when this intent declares an emergency, which changes how the engine prioritises it.
    /// </summary>
    public bool IsEmergency => Type is PilotIntentType.Mayday or PilotIntentType.PanPan;

    /// <summary>
    /// True when acting on this intent incorrectly would change a safety-critical clearance.
    /// These require higher confidence before the engine will act without clarification.
    /// </summary>
    public bool IsSafetyCritical => Type is
        PilotIntentType.ClearanceReadback
        or PilotIntentType.TakeoffReadback
        or PilotIntentType.LandingReadback
        or PilotIntentType.TaxiReadback
        or PilotIntentType.AltitudeReadback
        or PilotIntentType.HeadingReadback
        or PilotIntentType.ReadyForDeparture
        or PilotIntentType.ReportHoldingShort
        or PilotIntentType.ReportEstablished;
}

/// <summary>Which stage of the parsing pipeline produced an intent.</summary>
public enum IntentParserSource
{
    /// <summary>The fast local rule-based parser.</summary>
    Deterministic = 0,

    /// <summary>The language model, invoked because deterministic confidence was too low.</summary>
    LanguageModel,

    /// <summary>The language model was attempted and rejected; this is the fallback.</summary>
    DeterministicAfterModelRejection,

    /// <summary>Supplied directly by a test.</summary>
    Test,
}

/// <summary>Aviation values extracted from a pilot transmission.</summary>
public sealed record IntentEntities
{
    /// <summary>An entity set with nothing in it.</summary>
    public static IntentEntities Empty { get; } = new();

    /// <summary>Runway mentioned.</summary>
    public RunwayIdentifier? Runway { get; init; }

    /// <summary>Altitude or level mentioned.</summary>
    public Altitude? Altitude { get; init; }

    /// <summary>Squawk code mentioned.</summary>
    public SquawkCode? Squawk { get; init; }

    /// <summary>Frequency mentioned.</summary>
    public Frequency? Frequency { get; init; }

    /// <summary>Heading mentioned.</summary>
    public Heading? Heading { get; init; }

    /// <summary>Taxiway identifiers mentioned, in the order spoken.</summary>
    public IReadOnlyList<string> Taxiways { get; init; } = [];

    /// <summary>A named waypoint or fix, for a direct-to request.</summary>
    public string? Waypoint { get; init; }

    /// <summary>Approach name, for an approach request or established report.</summary>
    public string? ApproachName { get; init; }

    /// <summary>Stand name, for a taxi-to-stand request.</summary>
    public string? Stand { get; init; }

    /// <summary>
    /// Relative tail direction requested for pushback, normalised to <c>tail left</c> or
    /// <c>tail right</c>.
    /// </summary>
    public string? PushbackDirection { get; init; }

    /// <summary>Speed in knots, where one was mentioned.</summary>
    public int? SpeedKnots { get; init; }

    /// <summary>True when the pilot said they would hold short of something.</summary>
    public bool MentionsHoldShort { get; init; }

    /// <summary>The runway the pilot said they would hold short of.</summary>
    public RunwayIdentifier? HoldShortRunway { get; init; }

    /// <summary>The pilot said the flight is VFR (or a local flight).</summary>
    public bool DeclaresVfr { get; init; }

    /// <summary>The pilot asked for, or is flying, a touch and go.</summary>
    public bool TouchAndGo { get; init; }

    /// <summary>The option or low pass the pilot asked for on the next pass of the runway.</summary>
    public CircuitManoeuvre Manoeuvre { get; init; }

    /// <summary>The circuit direction the pilot named, if any.</summary>
    public CircuitSide? CircuitSide { get; init; }

    /// <summary>The ATIS information letter the pilot quoted, such as "I" for India.</summary>
    public string? InformationLetter { get; init; }

    /// <summary>True when no entity was extracted at all.</summary>
    public bool IsEmpty =>
        Runway is null && Altitude is null && Squawk is null && Frequency is null
        && Heading is null && Taxiways.Count == 0 && Waypoint is null
        && ApproachName is null && Stand is null && PushbackDirection is null
        && SpeedKnots is null && !MentionsHoldShort
        && !DeclaresVfr && !TouchAndGo && Manoeuvre == CircuitManoeuvre.None && CircuitSide is null && InformationLetter is null;
}

/// <summary>A manoeuvre the pilot asks for on the runway, other than a full-stop landing or a touch and go.</summary>
public enum CircuitManoeuvre
{
    /// <summary>None asked.</summary>
    None = 0,

    /// <summary>"The option": the pilot chooses between full stop, touch and go and stop-and-go.</summary>
    Option,

    /// <summary>A low pass over the runway, without touching down.</summary>
    LowPass,
}

/// <summary>The direction of turns in the aerodrome circuit.</summary>
public enum CircuitSide
{
    /// <summary>Left-hand circuit: turns to the left.</summary>
    Left = 1,

    /// <summary>Right-hand circuit: turns to the right.</summary>
    Right,
}

/// <summary>Validation and normalisation for directions used in pushback phraseology.</summary>
public static class PushbackDirections
{
    /// <summary>The aircraft tail moves to the pilot's left during pushback.</summary>
    public const string TailLeft = "tail left";

    /// <summary>The aircraft tail moves to the pilot's right during pushback.</summary>
    public const string TailRight = "tail right";

    /// <summary>Directions shown in settings.</summary>
    public static IReadOnlyList<string> All { get; } = [TailLeft, TailRight];

    /// <summary>Returns a canonical relative tail direction, or null when unsupported.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string compact = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return compact switch
        {
            "left" or "tailleft" or "tailtotheleft" => TailLeft,
            "right" or "tailright" or "tailtotheright" => TailRight,
            _ => null,
        };
    }
}

/// <summary>A value the pilot appears to be reading back.</summary>
public sealed record ParsedReadbackItem
{
    /// <summary>What kind of value this is.</summary>
    public required ReadbackItemType Type { get; init; }

    /// <summary>
    /// The value in normalised comparison form, ready to compare against
    /// <see cref="Sessions.ExpectedReadbackItem.ExpectedValue"/>.
    /// </summary>
    public required string NormalizedValue { get; init; }

    /// <summary>The raw text this was extracted from, for diagnostics.</summary>
    public string? SourceText { get; init; }
}
