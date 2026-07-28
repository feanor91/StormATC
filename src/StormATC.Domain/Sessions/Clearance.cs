using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Sessions;

/// <summary>
/// A clearance issued by a controller, with the exact values the pilot must comply with.
/// </summary>
/// <remarks>
/// <para>
/// A clearance is created only by the deterministic ATC engine. Nothing derived from a
/// language model response can construct or modify one — the model's job is to say the
/// clearance aloud, not to decide what it contains.
/// </para>
/// <para>
/// The clearance is not effective until any required readback has been correctly given; see
/// <see cref="IsAcknowledged"/>.
/// </para>
/// </remarks>
public sealed record Clearance
{
    /// <summary>Unique identifier for this clearance.</summary>
    public required Guid ClearanceId { get; init; }

    /// <summary>What kind of clearance this is.</summary>
    public required ClearanceType Type { get; init; }

    /// <summary>The controller that issued it.</summary>
    public required ControllerPosition IssuedBy { get; init; }

    /// <summary>When it was issued.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Destination aerodrome, for an IFR clearance.</summary>
    public string? ClearedToDestination { get; init; }

    /// <summary>Route or route summary as cleared.</summary>
    public string? ClearedRoute { get; init; }

    /// <summary>Initial climb altitude.</summary>
    public Altitude? InitialAltitude { get; init; }

    /// <summary>Cleared cruise level, where the clearance specifies one.</summary>
    public Altitude? CruiseAltitude { get; init; }

    /// <summary>Assigned runway, for takeoff, landing, taxi, or line-up clearances.</summary>
    public RunwayIdentifier? Runway { get; init; }

    /// <summary>Departure procedure name.</summary>
    public string? DepartureProcedure { get; init; }

    /// <summary>Approach name, for an approach clearance.</summary>
    public string? ApproachName { get; init; }

    /// <summary>Assigned transponder code.</summary>
    public SquawkCode? Squawk { get; init; }

    /// <summary>Frequency to contact next.</summary>
    public Frequency? NextFrequency { get; init; }

    /// <summary>Assigned heading.</summary>
    public Heading? AssignedHeading { get; init; }

    /// <summary>Runway the aircraft must hold short of.</summary>
    public RunwayIdentifier? HoldShortOfRunway { get; init; }

    /// <summary>Taxiways to follow, in order.</summary>
    public IReadOnlyList<string> TaxiRoute { get; init; } = [];

    /// <summary>
    /// Exact taxi-graph nodes traversed by this clearance, in order. This is retained separately
    /// from <see cref="TaxiRoute"/> so the learning UI can show progress and junction guidance
    /// without asking a language model to reconstruct the route.
    /// </summary>
    public IReadOnlyList<string> TaxiPathNodes { get; init; } = [];

    /// <summary>
    /// Taxiway name for each edge in <see cref="TaxiPathNodes"/>. This preserves the exact
    /// choice where scenery contains parallel graph links between the same two points.
    /// </summary>
    public IReadOnlyList<string> TaxiPathTaxiways { get; init; } = [];

    /// <summary>Total planned taxi distance in metres, when a graph route was available.</summary>
    public double? TaxiRouteLengthMeters { get; init; }

    /// <summary>Destination of a taxi clearance — a holding point or a stand name.</summary>
    public string? TaxiDestination { get; init; }

    /// <summary>Items the pilot is required to read back before this clearance takes effect.</summary>
    public IReadOnlyList<ReadbackItemType> RequiredReadbackItems { get; init; } = [];

    /// <summary>When the readback was accepted. Null while the readback is still outstanding.</summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>
    /// True when the clearance is fully effective: either it needed no readback, or the
    /// readback has been accepted.
    /// </summary>
    public bool IsAcknowledged =>
        RequiredReadbackItems.Count == 0 || AcknowledgedAt is not null;

    /// <summary>Marks the clearance as read back correctly.</summary>
    public Clearance Acknowledge(DateTimeOffset at) => this with { AcknowledgedAt = at };

    /// <summary>
    /// Produces the set of facts that must appear in the spoken response, keyed by the name the
    /// phraseology prompt uses. This is the immutable payload handed to the renderer, and the
    /// validator checks the rendered text against it afterwards.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToRequiredFacts()
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);

        if (ClearedToDestination is { Length: > 0 } destination)
        {
            facts["destination"] = destination;
        }

        if (ClearedRoute is { Length: > 0 } route)
        {
            facts["route"] = route;
        }

        if (InitialAltitude is Altitude initial)
        {
            facts["initialAltitude"] = initial.ToComparisonValue();
        }

        if (CruiseAltitude is Altitude cruise)
        {
            facts["cruiseAltitude"] = cruise.ToComparisonValue();
        }

        if (Runway is RunwayIdentifier runway)
        {
            facts["runway"] = runway.ToComparisonValue();
        }

        if (DepartureProcedure is { Length: > 0 } sid)
        {
            facts["departureProcedure"] = sid;
        }

        if (ApproachName is { Length: > 0 } approach)
        {
            facts["approach"] = approach;
        }

        if (Squawk is SquawkCode squawk)
        {
            facts["squawk"] = squawk.ToComparisonValue();
        }

        if (NextFrequency is Frequency frequency)
        {
            facts["frequency"] = frequency.ToComparisonValue();
        }

        if (AssignedHeading is Heading heading)
        {
            facts["heading"] = heading.ToComparisonValue();
        }

        if (HoldShortOfRunway is RunwayIdentifier holdShort)
        {
            facts["holdShortRunway"] = holdShort.ToComparisonValue();
        }

        if (TaxiRoute.Count > 0)
        {
            facts["taxiRoute"] = string.Join(",", TaxiRoute);
        }

        if (TaxiDestination is { Length: > 0 } taxiDestination)
        {
            facts["taxiDestination"] = taxiDestination;
        }

        return facts;
    }
}

/// <summary>
/// A readback the pilot currently owes, created when a clearance with mandatory items is issued.
/// </summary>
public sealed record PendingReadback
{
    /// <summary>The clearance awaiting acknowledgement.</summary>
    public required Guid ClearanceId { get; init; }

    /// <summary>Which clearance type is outstanding.</summary>
    public required ClearanceType ClearanceType { get; init; }

    /// <summary>The items required, with their expected values.</summary>
    public required IReadOnlyList<ExpectedReadbackItem> ExpectedItems { get; init; }

    /// <summary>When the clearance was issued.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>How many times the pilot has attempted this readback.</summary>
    public int AttemptCount { get; init; }

    /// <summary>
    /// Items correctly confirmed in an earlier partial readback.
    /// </summary>
    /// <remarks>
    /// A controller may correct only the missing or wrong parts of a readback. Keeping these
    /// items lets the next transmission complete that correction without forcing the pilot to
    /// repeat the entire clearance. If a later transmission explicitly contradicts one of
    /// these values, the evaluator removes it again.
    /// </remarks>
    public IReadOnlyList<ReadbackItemType> SatisfiedItems { get; init; } = [];

    /// <summary>
    /// True when the readback is mandatory before the clearance may be acted on. Safety-critical
    /// items — runway, altitude, squawk, hold-short — always are.
    /// </summary>
    public bool IsMandatory { get; init; } = true;

    /// <summary>Records another attempt and the items confirmed across all attempts so far.</summary>
    public PendingReadback WithAttempt(IReadOnlyList<ReadbackItemType>? satisfiedItems = null) =>
        this with
        {
            AttemptCount = AttemptCount + 1,
            SatisfiedItems = satisfiedItems is null
                ? SatisfiedItems
                : satisfiedItems.Distinct().ToArray(),
        };
}

/// <summary>A single value the pilot must read back, with the value the engine expects.</summary>
public sealed record ExpectedReadbackItem
{
    /// <summary>What kind of item this is.</summary>
    public required ReadbackItemType Type { get; init; }

    /// <summary>
    /// The normalised expected value, produced by the relevant value object's
    /// <c>ToComparisonValue()</c>. Comparison is against this, never against display text.
    /// </summary>
    public required string ExpectedValue { get; init; }

    /// <summary>How this item is spoken, used when generating a correction.</summary>
    public required string SpokenValue { get; init; }

    /// <summary>
    /// True when omitting this item invalidates the whole readback. Callsign, runway, altitude,
    /// squawk, frequency, and hold-short instructions are critical; a route summary is not.
    /// </summary>
    public bool IsCritical { get; init; } = true;
}
