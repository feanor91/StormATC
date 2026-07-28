using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// A nearby aircraft reported by Microsoft Flight Simulator.
/// </summary>
/// <remarks>
/// This is the simulator-ingest shape. The traffic provider turns it into the smaller domain
/// contact used by the ATC safety engine and resolves runway occupancy against loaded airport
/// geometry.
/// </remarks>
public sealed record MsfsTrafficSnapshot
{
    /// <summary>The SimConnect object identifier.</summary>
    public required uint ObjectId { get; init; }

    /// <summary>The best available ATC callsign, registration, or model label.</summary>
    public required string Callsign { get; init; }

    /// <summary>The simulator container title used by the injected or model-matched aircraft.</summary>
    public string ModelTitle { get; init; } = string.Empty;

    /// <summary>Current position.</summary>
    public required GeoPosition Position { get; init; }

    /// <summary>Indicated altitude in feet.</summary>
    public required double AltitudeFeet { get; init; }

    /// <summary>Ground speed in knots.</summary>
    public required double GroundSpeedKnots { get; init; }

    /// <summary>Magnetic heading in degrees.</summary>
    public double MagneticHeadingDegrees { get; init; }

    /// <summary>Vertical speed in feet per minute.</summary>
    public double VerticalSpeedFeetPerMinute { get; init; }

    /// <summary>Whether the simulator reports weight on wheels.</summary>
    public required bool IsOnGround { get; init; }

    /// <summary>Whether the simulator directly reports that the aircraft is on a runway.</summary>
    public bool IsOnAnyRunway { get; init; }

    /// <summary>True when the model title identifies an FSLTL aircraft package.</summary>
    public bool IsFslTlModel =>
        ModelTitle.Contains("FSLTL", StringComparison.OrdinalIgnoreCase);
}
