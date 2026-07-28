using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>
/// Everything StormATC knows about an aerodrome: runways, stands, frequencies, and the taxi
/// graph used to build routes.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately small model. StormATC does not bundle navigation data — it ships
/// fictional sample aerodromes and reads whatever the configured provider supplies. Anything
/// the data does not describe, the engine must refuse to invent, which is why every lookup
/// here returns null rather than a guess.
/// </para>
/// </remarks>
public sealed record AirportData
{
    /// <summary>ICAO identifier.</summary>
    public required string Icao { get; init; }

    /// <summary>Aerodrome name.</summary>
    public required string Name { get; init; }

    /// <summary>Reference point, used for proximity checks.</summary>
    public required GeoPosition ReferencePosition { get; init; }

    /// <summary>Elevation in feet.</summary>
    public required int ElevationFeet { get; init; }

    /// <summary>Magnetic variation in degrees, east positive.</summary>
    public double MagneticVariationDegrees { get; init; }

    /// <summary>
    /// True when this aerodrome is fictional sample data rather than real-world information.
    /// StormATC's bundled aerodromes are all fictional and the UI says so.
    /// </summary>
    public bool IsFictional { get; init; } = true;

    /// <summary>Published runways.</summary>
    public IReadOnlyList<RunwayData> Runways { get; init; } = [];

    /// <summary>Parking stands and gates.</summary>
    public IReadOnlyList<StandData> Stands { get; init; } = [];

    /// <summary>Controller frequencies.</summary>
    public required AirportFrequencies Frequencies { get; init; }

    /// <summary>The taxi graph. Null when the source supplied no taxiway layout.</summary>
    public TaxiGraph? TaxiGraph { get; init; }

    /// <summary>
    /// True when this aerodrome can produce real taxi routes. When false the engine must fall
    /// back to a clearly labelled limited mode rather than making a route up.
    /// </summary>
    public bool SupportsTaxiRouting => TaxiGraph is { Nodes.Count: > 0 };

    /// <summary>Finds a runway by identifier, or null when this aerodrome has no such runway.</summary>
    public RunwayData? FindRunway(RunwayIdentifier identifier)
    {
        foreach (RunwayData runway in Runways)
        {
            if (runway.Identifier == identifier)
            {
                return runway;
            }
        }

        return null;
    }

    /// <summary>Finds a stand by name, case-insensitively.</summary>
    public StandData? FindStand(string name)
    {
        foreach (StandData stand in Stands)
        {
            if (string.Equals(stand.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return stand;
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the runway best aligned with the reported surface wind.
    /// </summary>
    /// <remarks>
    /// Chooses the runway whose centreline is closest to the wind direction, which is the
    /// normal into-wind selection. With calm or unknown wind, returns the first runway marked
    /// as preferred, or simply the first runway. This is a simulation heuristic, not an
    /// operational runway-selection procedure.
    /// </remarks>
    public RunwayData? SelectActiveRunway(Heading? windDirection)
    {
        if (Runways.Count == 0)
        {
            return null;
        }

        if (windDirection is not Heading wind)
        {
            foreach (RunwayData runway in Runways)
            {
                if (runway.IsPreferred)
                {
                    return runway;
                }
            }

            return Runways[0];
        }

        RunwayData best = Runways[0];
        int bestDifference = int.MaxValue;

        foreach (RunwayData runway in Runways)
        {
            int difference = runway.Identifier.CenterlineHeading.DifferenceTo(wind);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                best = runway;
            }
        }

        return best;
    }
}

/// <summary>A single runway direction.</summary>
public sealed record RunwayData
{
    /// <summary>Runway designator.</summary>
    public required RunwayIdentifier Identifier { get; init; }

    /// <summary>Threshold position.</summary>
    public required GeoPosition ThresholdPosition { get; init; }

    /// <summary>Opposite-end position, used to compute the centreline.</summary>
    public required GeoPosition EndPosition { get; init; }

    /// <summary>Length in feet.</summary>
    public required int LengthFeet { get; init; }

    /// <summary>Width in feet.</summary>
    public int WidthFeet { get; init; }

    /// <summary>Surface description, for display only.</summary>
    public string? Surface { get; init; }

    /// <summary>True when this direction is the aerodrome's normal preference.</summary>
    public bool IsPreferred { get; init; }

    /// <summary>Instrument approaches published for this runway.</summary>
    public IReadOnlyList<string> Approaches { get; init; } = [];

    /// <summary>Taxi-graph node names that serve as holding points for this runway.</summary>
    public IReadOnlyList<string> HoldingPointNodes { get; init; } = [];

    /// <summary>Taxi-graph node names usable as exits after landing.</summary>
    public IReadOnlyList<string> ExitNodes { get; init; } = [];

    /// <summary>True magnetic bearing of the centreline, derived from the threshold geometry.</summary>
    public Heading CenterlineBearing => ThresholdPosition.TrueBearingTo(EndPosition);

    /// <summary>True when the named approach is published for this runway.</summary>
    public bool PublishesApproach(string approachName)
    {
        foreach (string approach in Approaches)
        {
            if (approach.Contains(approachName, StringComparison.OrdinalIgnoreCase)
                || approachName.Contains(approach, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A parking stand or gate.</summary>
public sealed record StandData
{
    /// <summary>Stand name, for example <c>A1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Stand position.</summary>
    public required GeoPosition Position { get; init; }

    /// <summary>The taxi-graph node this stand connects to.</summary>
    public required string TaxiNodeName { get; init; }

    /// <summary>Aircraft size categories this stand accepts, for display only.</summary>
    public IReadOnlyList<string> SuitableFor { get; init; } = [];

    /// <summary>True when pushback is required to leave this stand.</summary>
    public bool RequiresPushback { get; init; } = true;
}

/// <summary>The controller frequencies published for an aerodrome.</summary>
public sealed record AirportFrequencies
{
    /// <summary>Clearance delivery.</summary>
    public Frequency? ClearanceDelivery { get; init; }

    /// <summary>Ground movement control.</summary>
    public Frequency? Ground { get; init; }

    /// <summary>Aerodrome control.</summary>
    public Frequency? Tower { get; init; }

    /// <summary>Departure control.</summary>
    public Frequency? Departure { get; init; }

    /// <summary>Approach control.</summary>
    public Frequency? Approach { get; init; }

    /// <summary>Area control.</summary>
    public Frequency? Center { get; init; }

    /// <summary>Automatic terminal information service.</summary>
    public Frequency? Atis { get; init; }

    /// <summary>
    /// Returns the frequency for a controller position, or null when the aerodrome does not
    /// publish one.
    /// </summary>
    /// <remarks>
    /// Where a position is not staffed, callers fall back to a combined position rather than
    /// inventing a frequency. See <see cref="ResolveWithFallback"/>.
    /// </remarks>
    public Frequency? For(ControllerPosition position) => position switch
    {
        ControllerPosition.Atis => Atis,
        ControllerPosition.ClearanceDelivery => ClearanceDelivery,
        ControllerPosition.Ground => Ground,
        ControllerPosition.Tower => Tower,
        ControllerPosition.Departure => Departure,
        ControllerPosition.Approach => Approach,
        ControllerPosition.Center => Center,
        _ => null,
    };

    /// <summary>
    /// Resolves a frequency, falling back through the positions that would realistically
    /// combine at a quiet aerodrome: delivery folds into ground, ground and departure into
    /// tower, approach into centre.
    /// </summary>
    public Frequency? ResolveWithFallback(ControllerPosition position) => position switch
    {
        ControllerPosition.Atis => Atis,
        ControllerPosition.ClearanceDelivery => ClearanceDelivery ?? Ground ?? Tower,
        ControllerPosition.Ground => Ground ?? Tower,
        ControllerPosition.Tower => Tower,
        ControllerPosition.Departure => Departure ?? Approach ?? Center,
        ControllerPosition.Approach => Approach ?? Center,
        ControllerPosition.Center => Center ?? Approach,
        _ => null,
    };
}
