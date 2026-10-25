using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>What makes an area unfit to cross.</summary>
public enum HazardKind
{
    /// <summary>A military area (restricted, danger or reserved) that is active.</summary>
    MilitaryArea,

    /// <summary>A weather area to keep clear of, such as a thunderstorm.</summary>
    Weather,
}

/// <summary>A circular area an en-route flight must be kept out of, with the levels it covers.</summary>
public sealed record HazardArea
{
    /// <summary>The name a controller says: "R 45", "TSRA".</summary>
    public required string Name { get; init; }

    /// <summary>What kind of area it is.</summary>
    public required HazardKind Kind { get; init; }

    /// <summary>The centre of the area.</summary>
    public required GeoPosition Center { get; init; }

    /// <summary>The radius, in nautical miles.</summary>
    public required double RadiusNauticalMiles { get; init; }

    /// <summary>The lowest level covered, in feet.</summary>
    public double FloorFeet { get; init; }

    /// <summary>The highest level covered, in feet.</summary>
    public double CeilingFeet { get; init; } = 99_999;

    /// <summary>False when the area is not active and can be crossed.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>True when an aircraft at this altitude is at a level the area covers.</summary>
    /// <param name="altitudeFeet">The altitude.</param>
    public bool CoversAltitude(double altitudeFeet) => altitudeFeet >= FloorFeet && altitudeFeet <= CeilingFeet;

    /// <summary>The shortest distance from the area's centre to the straight path between two positions, in nautical miles.</summary>
    /// <param name="from">The start of the path.</param>
    /// <param name="to">The end of the path.</param>
    public double DistanceFromCenterToPath(GeoPosition from, GeoPosition to)
    {
        // A flat plane around the centre is exact enough at the scale of an area and a path of a few dozen miles.
        (double ax, double ay) = Local(from);
        (double bx, double by) = Local(to);

        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared < 1e-9 ? 0 : Math.Clamp(-((ax * dx) + (ay * dy)) / lengthSquared, 0, 1);
        double px = ax + (t * dx);
        double py = ay + (t * dy);
        return Math.Sqrt((px * px) + (py * py));
    }

    /// <summary>True when the path between two positions passes within the area, widened by a margin.</summary>
    /// <param name="from">The start of the path.</param>
    /// <param name="to">The end of the path.</param>
    /// <param name="marginNauticalMiles">The margin kept around the area.</param>
    public bool CrossesPath(GeoPosition from, GeoPosition to, double marginNauticalMiles) =>
        DistanceFromCenterToPath(from, to) <= RadiusNauticalMiles + marginNauticalMiles;

    private (double X, double Y) Local(GeoPosition position)
    {
        double y = (position.Latitude - Center.Latitude) * 60;
        double x = (position.Longitude - Center.Longitude) * 60 * Math.Cos(Center.Latitude * Math.PI / 180);
        return (x, y);
    }
}
