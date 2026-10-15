using StormATC.Domain.Intents;

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
public sealed record VfrAerodromeProfile
{
    /// <summary>The ICAO code.</summary>
    public required string Icao { get; init; }

    /// <summary>The aerodrome name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The published reporting points, in the order they are used on arrival.</summary>
    public IReadOnlyList<VfrReportingPoint> ReportingPoints { get; init; } = [];

    /// <summary>The circuit height in feet above mean sea level, when published.</summary>
    public int? CircuitHeightFeetAmsl { get; init; }

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
