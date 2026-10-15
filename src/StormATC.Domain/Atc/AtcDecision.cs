using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

/// <summary>
/// The authoritative decision engine. Given everything that is true, decides what is permitted.
/// </summary>
/// <remarks>
/// <para>
/// This interface is the boundary that makes StormATC trustworthy. Implementations must be
/// pure functions of their input: no network calls, no database access, no clock reads beyond
/// the supplied <see cref="AtcEvaluationContext.CurrentTime"/>, no I/O of any kind. That
/// constraint is what allows the whole clearance model to be tested exhaustively offline.
/// </para>
/// </remarks>
public interface IAtcStateEngine
{
    /// <summary>Evaluates a pilot transmission and returns what the controller may do about it.</summary>
    AtcDecision Evaluate(AtcEvaluationContext context);

    /// <summary>
    /// Evaluates a change in aircraft state with no pilot transmission — used to detect events
    /// the engine must notice on its own, such as becoming airborne, touching down, or crossing
    /// a hold-short line without clearance.
    /// </summary>
    AtcDecision EvaluateStateChange(AtcEvaluationContext context);
}

/// <summary>Everything the engine needs to make one decision.</summary>
public sealed record AtcEvaluationContext
{
    /// <summary>The session as it stands now.</summary>
    public required AtcSession Session { get; init; }

    /// <summary>The latest aircraft snapshot.</summary>
    public required AircraftState AircraftState { get; init; }

    /// <summary>The active flight plan, when one is loaded.</summary>
    public FlightPlan? FlightPlan { get; init; }

    /// <summary>The parsed pilot intent. Null when evaluating a state change.</summary>
    public PilotIntent? Intent { get; init; }

    /// <summary>The raw transcript, for readback extraction.</summary>
    public string Transcript { get; init; } = string.Empty;

    /// <summary>Data for the departure aerodrome.</summary>
    public AirportData? DepartureAirport { get; init; }

    /// <summary>Data for the destination aerodrome.</summary>
    public AirportData? DestinationAirport { get; init; }

    /// <summary>
    /// The VFR procedures of the aerodrome in use (reporting points, circuit), or null when no
    /// profile exists for it. The engine falls back to generic phraseology without one.
    /// </summary>
    public VfrAerodromeProfile? VfrProfile { get; init; }

    /// <summary>Simulated traffic relevant to separation and runway occupancy.</summary>
    public IReadOnlyList<TrafficContact> Traffic { get; init; } = [];

    /// <summary>Weather, where a provider supplied it. Null means no weather may be quoted.</summary>
    public WeatherSnapshot? Weather { get; init; }

    /// <summary>Time of evaluation. The engine must not read the system clock.</summary>
    public required DateTimeOffset CurrentTime { get; init; }

    /// <summary>
    /// A transponder code fixed by the scenario, overriding the engine's own allocation.
    /// </summary>
    /// <remarks>
    /// This exists so a scripted demonstration or a regression test can pin the squawk and
    /// still exercise the real clearance path. When null the engine allocates a code itself.
    /// </remarks>
    public SquawkCode? PreassignedSquawk { get; init; }

    /// <summary>How strictly the engine enforces procedure.</summary>
    public RealismProfile Realism { get; init; } = RealismProfile.Standard;

    /// <summary>
    /// Relative tail direction used when the pilot does not request one explicitly.
    /// </summary>
    public string DefaultPushbackDirection { get; init; } = PushbackDirections.TailLeft;

    /// <summary>
    /// The aerodrome relevant to the current phase — departure while outbound, destination
    /// once the aircraft is arriving.
    /// </summary>
    public AirportData? RelevantAirport => Session.Phase switch
    {
        FlightPhase.Descent or FlightPhase.Arrival or FlightPhase.Approach or FlightPhase.Final
            or FlightPhase.Landed or FlightPhase.TaxiIn or FlightPhase.Parked
            or FlightPhase.GoAround or FlightPhase.Circuit => DestinationAirport ?? DepartureAirport,
        _ => DepartureAirport,
    };
}

/// <summary>How strictly the engine enforces procedure.</summary>
public sealed record RealismProfile
{
    /// <summary>The default profile: realistic, but forgiving of minor phraseology slips.</summary>
    public static RealismProfile Standard { get; } = new();

    /// <summary>A relaxed profile for casual use — readbacks are requested but not enforced.</summary>
    public static RealismProfile Relaxed { get; } = new()
    {
        RequireReadbackBeforeProgressing = false,
        RequireCorrectCallsign = false,
        EnforceHoldShort = false,
        TranscriptTolerance = TranscriptToleranceMode.Relaxed,
    };

    /// <summary>A strict profile for realism-focused pilots.</summary>
    public static RealismProfile Strict { get; } = new()
    {
        RequireReadbackBeforeProgressing = true,
        RequireCorrectCallsign = true,
        EnforceHoldShort = true,
        RequireCheckInOnFrequencyChange = true,
        AcceptAcknowledgementAsReadback = false,
        MinimumIntentConfidence = 0.75,
        TranscriptTolerance = TranscriptToleranceMode.Strict,
    };

    /// <summary>
    /// Resolves a profile by name, falling back to <see cref="Standard"/> for anything else.
    /// </summary>
    /// <remarks>
    /// A profile is a record rather than an enum because each preset is a bundle of individual
    /// rules the user can also set one by one. This is the one place a configured name becomes a
    /// profile, so an unrecognised value degrades identically wherever it comes from.
    /// </remarks>
    public static RealismProfile FromName(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "RELAXED" => Relaxed,
        "STRICT" => Strict,
        _ => Standard,
    };

    /// <summary>True when a name matches one of the presets.</summary>
    public static bool IsKnownName(string? name) =>
        name?.Trim().ToUpperInvariant() is "RELAXED" or "STANDARD" or "STRICT";

    /// <summary>
    /// When true, an outstanding mandatory readback blocks further clearances. This is the
    /// behaviour real ATC relies on and the default.
    /// </summary>
    public bool RequireReadbackBeforeProgressing { get; init; } = true;

    /// <summary>When true, a transmission with the wrong callsign is refused.</summary>
    public bool RequireCorrectCallsign { get; init; } = true;

    /// <summary>When true, crossing a hold-short line without clearance raises a warning.</summary>
    public bool EnforceHoldShort { get; init; } = true;

    /// <summary>When true, the pilot must check in after every frequency change.</summary>
    public bool RequireCheckInOnFrequencyChange { get; init; }

    /// <summary>
    /// When true, a plain "roger" or "bien reçu" settles an outstanding readback once the pilot
    /// has already tried and failed to read it back. Not real practice, but it keeps a simulation
    /// from stalling on a readback the pilot cannot reproduce, such as a long taxi route. The
    /// first "roger" is still sent back for a real readback, and the Strict profile never
    /// allows it.
    /// </summary>
    public bool AcceptAcknowledgementAsReadback { get; init; } = true;

    /// <summary>
    /// Below this confidence the engine asks the pilot to repeat rather than acting on a
    /// safety-critical intent.
    /// </summary>
    public double MinimumIntentConfidence { get; init; } = 0.6;

    /// <summary>
    /// How ambiguous speech-to-text values are handled during readback comparison. Every mode
    /// accepts equivalent written/spoken aviation values; the difference is whether an
    /// unanchored parser guess is treated as a contradiction or as an item to ask for again.
    /// </summary>
    public TranscriptToleranceMode TranscriptTolerance { get; init; } =
        TranscriptToleranceMode.Standard;
}

/// <summary>How forgiving readback interpretation is of ambiguous speech-recognition output.</summary>
public enum TranscriptToleranceMode
{
    /// <summary>
    /// Accept common transcript variants, make the callsign optional, and avoid treating
    /// unannounced parser guesses as explicit contradictions.
    /// </summary>
    Relaxed = 0,

    /// <summary>
    /// Recommended: accept safe aviation equivalents and partial corrections, while reserving
    /// “negative” for values the pilot clearly announced.
    /// </summary>
    Standard,

    /// <summary>
    /// Treat every structured value returned by the parser as intentional and require the
    /// configured callsign/readback rules exactly.
    /// </summary>
    Strict,
}

/// <summary>A simulated aircraft near ours.</summary>
public sealed record TrafficContact
{
    /// <summary>Traffic callsign.</summary>
    public required string Callsign { get; init; }

    /// <summary>Position.</summary>
    public required GeoPosition Position { get; init; }

    /// <summary>Altitude in feet.</summary>
    public required double AltitudeFeet { get; init; }

    /// <summary>Ground speed in knots.</summary>
    public double GroundSpeedKnots { get; init; }

    /// <summary>True when this traffic is on a runway surface.</summary>
    public bool IsOnRunway { get; init; }

    /// <summary>Which runway it occupies, when it is on one.</summary>
    public RunwayIdentifier? OccupiedRunway { get; init; }
}

/// <summary>
/// Observed weather. Null anywhere in the pipeline means "no weather source" — the engine and
/// the phraseology renderer must then omit wind and QNH rather than inventing values.
/// </summary>
public sealed record WeatherSnapshot
{
    /// <summary>Surface wind direction, magnetic.</summary>
    public Heading? WindDirection { get; init; }

    /// <summary>Surface wind speed in knots.</summary>
    public int? WindSpeedKnots { get; init; }

    /// <summary>Altimeter setting in hectopascals.</summary>
    public int? QnhHectopascals { get; init; }

    /// <summary>Visibility in metres.</summary>
    public int? VisibilityMeters { get; init; }

    /// <summary>Ambient temperature in degrees Celsius.</summary>
    public int? TemperatureCelsius { get; init; }

    /// <summary>Where this observation came from, for the diagnostics page.</summary>
    public string? Source { get; init; }

    /// <summary>True when there is enough here to speak a wind check.</summary>
    public bool HasWind => WindDirection is not null && WindSpeedKnots is not null;
}

/// <summary>
/// What the controller is permitted to do in response to a transmission.
/// </summary>
/// <remarks>
/// This is the only channel through which session state changes. The coordinator applies
/// <see cref="NewPhase"/>, <see cref="IssuedClearance"/>, and <see cref="Handoff"/> exactly as
/// given; it never synthesises a transition of its own, and neither does anything downstream
/// of the language model.
/// </remarks>
public sealed record AtcDecision
{
    /// <summary>Whether the transmission was accepted.</summary>
    public required bool Accepted { get; init; }

    /// <summary>The kind of response the controller should make.</summary>
    public required AtcResponseType ResponseType { get; init; }

    /// <summary>Phase change to apply, when the decision causes one.</summary>
    public FlightPhase? NewPhase { get; init; }

    /// <summary>A change of flight rules, such as the pilot declaring VFR. Null leaves it unchanged.</summary>
    public FlightRules? NewFlightRules { get; init; }

    /// <summary>The circuit leg reached. Null leaves it unchanged.</summary>
    public CircuitLeg? NewCircuitLeg { get; init; }

    /// <summary>A touch-and-go request or its cancellation. Null leaves it unchanged.</summary>
    public bool? NewTouchAndGoRequested { get; init; }

    /// <summary>The runway in use for a VFR circuit or arrival. Null leaves it unchanged.</summary>
    public RunwayIdentifier? NewArrivalRunway { get; init; }

    /// <summary>Clearance to issue, when the decision grants one.</summary>
    public Clearance? IssuedClearance { get; init; }

    /// <summary>Readback the pilot will owe after this response.</summary>
    public PendingReadback? RequiredReadback { get; init; }

    /// <summary>True when an outstanding readback has been satisfied by this transmission.</summary>
    public bool ClearsPendingReadback { get; init; }

    /// <summary>Handoff to perform.</summary>
    public ControllerHandoff? Handoff { get; init; }

    /// <summary>
    /// The facts the response must contain, already normalised. This is the immutable payload
    /// the phraseology renderer is allowed to express — and nothing else.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApprovedFacts { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Things the response must not say. Checked against the rendered text before it is spoken,
    /// so a model that adds "cleared for takeoff" to a taxi instruction is caught.
    /// </summary>
    public IReadOnlyList<string> ProhibitedFacts { get; init; } = [];

    /// <summary>Warnings raised by this decision.</summary>
    public IReadOnlyList<AtcWarning> Warnings { get; init; } = [];

    /// <summary>Why the transmission was refused, when it was.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>
    /// Whether the language model may be asked to phrase this response. False for corrections
    /// and refusals, where the exact deterministic wording matters more than natural delivery.
    /// </summary>
    public bool AllowLanguageModelPhraseology { get; init; } = true;

    /// <summary>
    /// The wording to use when no model is configured, the model fails, or its output fails
    /// validation. Always populated — there is no path where the controller has nothing to say.
    /// </summary>
    public required string FallbackPhraseology { get; init; }

    /// <summary>The readback assessment, when this decision evaluated one.</summary>
    public ReadbackAssessment? ReadbackAssessment { get; init; }

    /// <summary>True when this decision alters clearance state.</summary>
    public bool ChangesClearanceState =>
        IssuedClearance is not null
        || RequiredReadback is not null
        || ClearsPendingReadback
        || Handoff is not null
        || NewPhase is not null
        || NewFlightRules is not null
        || NewCircuitLeg is not null
        || NewTouchAndGoRequested is not null
        || NewArrivalRunway is not null;

    /// <summary>Builds a refusal.</summary>
    public static AtcDecision Reject(string reason, string fallbackPhraseology) => new()
    {
        Accepted = false,
        ResponseType = AtcResponseType.Rejection,
        RejectionReason = reason,
        FallbackPhraseology = fallbackPhraseology,

        // Refusals are spoken verbatim. A model asked to "phrase this naturally" can soften a
        // refusal into something that reads like permission, which is exactly the failure mode
        // this whole architecture exists to prevent.
        AllowLanguageModelPhraseology = false,
    };

    /// <summary>Builds a decision that produces no controller transmission at all.</summary>
    public static AtcDecision Silent() => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.None,
        FallbackPhraseology = string.Empty,
        AllowLanguageModelPhraseology = false,
    };
}
