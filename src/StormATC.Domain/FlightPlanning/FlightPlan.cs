using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.FlightPlanning;

/// <summary>
/// A normalised operational flight plan, independent of where it came from.
/// </summary>
/// <remarks>
/// <para>
/// SimBrief is the first supported source, but nothing in this type is SimBrief-shaped. A
/// future importer for a different planner populates the same fields, and the ATC engine never
/// learns which source was used.
/// </para>
/// <para>
/// Almost everything except the callsign and the departure airport is nullable, because real
/// flight plans genuinely omit fields — a VFR plan has no SID, a short hop has no alternate,
/// and a plan filed before runway assignment has no departure runway. Code that consumes this
/// must degrade rather than assume.
/// </para>
/// </remarks>
public sealed record FlightPlan
{
    /// <summary>StormATC's own identifier for this imported plan.</summary>
    public required string FlightPlanId { get; init; }

    /// <summary>The callsign to be used on the radio.</summary>
    public required Callsign Callsign { get; init; }

    /// <summary>Airline flight number, where the plan distinguishes it from the callsign.</summary>
    public string? FlightNumber { get; init; }

    /// <summary>ICAO aircraft type designator, for example <c>A320</c>.</summary>
    public string? AircraftIcaoType { get; init; }

    /// <summary>Aircraft registration, where supplied.</summary>
    public string? Registration { get; init; }

    /// <summary>ICAO code of the departure aerodrome.</summary>
    public required string DepartureIcao { get; init; }

    /// <summary>ICAO code of the destination aerodrome.</summary>
    public required string DestinationIcao { get; init; }

    /// <summary>Filed alternates, in order of preference. May be empty.</summary>
    public IReadOnlyList<string> AlternateIcaos { get; init; } = [];

    /// <summary>Planned departure runway, where the plan specifies one.</summary>
    public RunwayIdentifier? DepartureRunway { get; init; }

    /// <summary>Planned arrival runway, where the plan specifies one.</summary>
    public RunwayIdentifier? ArrivalRunway { get; init; }

    /// <summary>Standard instrument departure identifier.</summary>
    public string? StandardInstrumentDeparture { get; init; }

    /// <summary>Standard terminal arrival route identifier.</summary>
    public string? StandardTerminalArrival { get; init; }

    /// <summary>Planned instrument approach, for example <c>ILS 36</c>.</summary>
    public string? Approach { get; init; }

    /// <summary>The filed route string as written on the plan.</summary>
    public string? Route { get; init; }

    /// <summary>The route broken into individual legs, where the source provided a navlog.</summary>
    public IReadOnlyList<RouteLeg> RouteLegs { get; init; } = [];

    /// <summary>Planned cruise level.</summary>
    public Altitude? CruiseAltitude { get; init; }

    /// <summary>Estimated block time — off-blocks to on-blocks.</summary>
    public TimeSpan? EstimatedBlockTime { get; init; }

    /// <summary>Planned block fuel in kilograms, where supplied.</summary>
    public double? PlannedBlockFuelKilograms { get; init; }

    /// <summary>Planned trip fuel in kilograms, where supplied.</summary>
    public double? PlannedTripFuelKilograms { get; init; }

    /// <summary>When the source system generated this plan.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>Identifier of the importing provider, for example <c>SimBrief</c> or <c>Sample</c>.</summary>
    public required string SourceIdentifier { get; init; }

    /// <summary>
    /// Local path to the cached raw source payload, kept for diagnostics when a parse looks
    /// wrong. Null when the plan did not come from a downloaded document.
    /// </summary>
    public string? RawSourceCachePath { get; init; }

    /// <summary>
    /// True when the plan is old enough that the user should be prompted to re-import.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="maximumAge">How old a plan may be before it is considered stale.</param>
    public bool IsStale(DateTimeOffset now, TimeSpan maximumAge) =>
        GeneratedAt is DateTimeOffset generated && now - generated > maximumAge;

    /// <summary>A short human-readable summary, for example <c>STORM123 STMR → STMD</c>.</summary>
    public string ToSummary() => $"{Callsign} {DepartureIcao} → {DestinationIcao}";
}

/// <summary>A single leg of a filed route.</summary>
public sealed record RouteLeg
{
    /// <summary>Waypoint, navaid, or airport identifier.</summary>
    public required string Identifier { get; init; }

    /// <summary>Airway or procedure joining the previous fix to this one, where known.</summary>
    public string? Via { get; init; }

    /// <summary>Position of the fix, where the source supplied coordinates.</summary>
    public GeoPosition? Position { get; init; }

    /// <summary>Planned level over the fix.</summary>
    public Altitude? PlannedAltitude { get; init; }

    /// <summary>Cumulative distance from origin in nautical miles.</summary>
    public double? CumulativeDistanceNauticalMiles { get; init; }

    /// <summary>Stage of flight this leg belongs to, as labelled by the source.</summary>
    public string? Stage { get; init; }
}
