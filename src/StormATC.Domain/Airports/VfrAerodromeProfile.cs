using StormATC.Domain.Intents;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>A published VFR reporting point of an aerodrome, such as E ("Écho") at Lognes.</summary>
/// <param name="Name">The chart designator, such as <c>E</c>.</param>
/// <param name="SpokenFrench">How a French controller says it, such as <c>Écho</c>.</param>
/// <param name="SpokenEnglish">How an English controller says it, such as <c>Echo</c>.</param>
public sealed record VfrReportingPoint(string Name, string SpokenFrench, string SpokenEnglish)
{
    /// <summary>True when <paramref name="text"/> names this point by designator or spoken name.</summary>
    public bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        return trimmed.Equals(Name, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(SpokenFrench, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(SpokenEnglish, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// What a controlled aerodrome publishes for VFR traffic that the simulator does not know:
/// reporting points, the circuit, and the preferred runway direction.
/// </summary>
/// <remarks>
/// Runway geometry, taxiways, parking and frequencies come from the simulator. This profile adds
/// only the VFR procedures, so an aerodrome without one still works with generic phraseology.
/// </remarks>
/// <summary>A frequency an aerodrome publishes.</summary>
/// <param name="Service">The service: APP, TWR, GND, ATIS, PAR, SRE, A/A…</param>
/// <param name="Frequency">The frequency.</param>
/// <param name="Remark">What the aerodrome says about it (transit, recovery, particular…), or empty.</param>
public sealed record AerodromeFrequency(string Service, Frequency Frequency, string Remark);

/// <summary>One direction of a runway as the chart prints it.</summary>
/// <param name="Identifier">The designator, such as <c>13</c> or <c>31R</c>.</param>
/// <param name="QfuDegrees">The magnetic heading of that direction.</param>
public sealed record VfrRunwayDirection(RunwayIdentifier Identifier, int QfuDegrees);

public sealed record VfrAerodromeProfile
{
    /// <summary>The ICAO code.</summary>
    public required string Icao { get; init; }

    /// <summary>The aerodrome name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The air traffic service the aerodrome publishes: <c>None</c> (auto-information, no controller),
    /// <c>Afis</c>, <c>Tower</c>, <c>TowerAndGround</c>…; null when the profile does not say.
    /// </summary>
    public string? ServiceKind { get; init; }

    /// <summary>The air-air (auto-information) frequency, when the profile gives one.</summary>
    public Frequency? AirAirFrequency { get; init; }

    /// <summary>
    /// True when the profile says the aerodrome has no air traffic service: pilots make blind calls on
    /// the air-air frequency and nobody answers.
    /// </summary>
    public bool HasNoAtc => string.Equals(ServiceKind, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the profile says the aerodrome is a military base (its service kind starts with "Military").</summary>
    public bool IsMilitary => ServiceKind?.StartsWith("Military", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The frequencies the profile lists, with the service each belongs to.</summary>
    public IReadOnlyList<AerodromeFrequency> Frequencies { get; init; } = [];

    /// <summary>The published reporting points, in the order they are used on arrival.</summary>
    public IReadOnlyList<VfrReportingPoint> ReportingPoints { get; init; } = [];

    /// <summary>The circuit height in feet above mean sea level, when published.</summary>
    public int? CircuitHeightFeetAmsl { get; init; }

    /// <summary>Every runway direction the chart prints, each with its magnetic heading.</summary>
    public IReadOnlyList<VfrRunwayDirection> Runways { get; init; } = [];

    /// <summary>The magnetic variation in degrees, positive east, when the profile gives it.</summary>
    public double? MagneticVariationDegrees { get; init; }

    /// <summary>
    /// The runway direction to use: the one most into the wind, or the preferred one (otherwise the first listed) when
    /// the wind is light or not known. This is what a controller would pick with no other constraint: it is not the
    /// runway really in use.
    /// </summary>
    /// <param name="windFromDegreesTrue">Where the wind blows from, in degrees true as a METAR gives it.</param>
    /// <param name="windSpeedKnots">The wind speed.</param>
    public RunwayIdentifier? SelectRunway(int? windFromDegreesTrue, int? windSpeedKnots)
    {
        if (Runways.Count == 0)
        {
            return null;
        }

        if (windFromDegreesTrue is int from && windSpeedKnots is > LightWindKnots)
        {
            double magnetic = from - (MagneticVariationDegrees ?? 0);
            return Runways
                .MaxBy(r => Math.Cos((magnetic - r.QfuDegrees) * Math.PI / 180))!
                .Identifier;
        }

        VfrRunwayDirection? preferred = PreferredQfu is int qfu
            ? Runways.FirstOrDefault(r => r.QfuDegrees == qfu)
            : null;
        return (preferred ?? Runways[0]).Identifier;
    }

    private const int LightWindKnots = 3;

    /// <summary>The runway direction preferred when the wind allows either, in degrees magnetic.</summary>
    public int? PreferredQfu { get; init; }

    /// <summary>
    /// The side of the runways on which the circuit lies, as a compass bearing in degrees: 0 for
    /// north of the runways, 180 for south. Null when the aerodrome does not publish one.
    /// </summary>
    /// <remarks>
    /// Whether that is a left-hand or right-hand circuit then depends on the runway in use: a
    /// circuit north of the runways is left-hand for a runway heading east and right-hand for one
    /// heading west.
    /// </remarks>
    public int? CircuitSideBearing { get; init; }

    /// <summary>Finds a reporting point by designator or spoken name.</summary>
    public VfrReportingPoint? FindPoint(string? text) =>
        ReportingPoints.FirstOrDefault(point => point.Matches(text));

    /// <summary>
    /// The circuit direction for a runway, or null when the profile does not say which side the
    /// circuit is on.
    /// </summary>
    /// <param name="runwayHeadingDegrees">The runway's magnetic heading (its QFU).</param>
    public CircuitSide? CircuitSideFor(int runwayHeadingDegrees)
    {
        if (CircuitSideBearing is not int bearing)
        {
            return null;
        }

        // The signed angle from the runway heading to the circuit side: negative when the
        // circuit lies to the left of the direction of landing.
        int difference = (((bearing - runwayHeadingDegrees) % 360) + 540) % 360 - 180;

        // A circuit lies beside the runway. Ahead of or behind the landing direction there is no
        // left or right to speak of, so the profile does not answer.
        return Math.Abs(difference) switch
        {
            < 10 or > 170 => null,
            _ => difference < 0 ? CircuitSide.Left : CircuitSide.Right,
        };
    }
}
