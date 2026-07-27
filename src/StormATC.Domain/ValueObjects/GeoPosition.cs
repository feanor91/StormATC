using System.Globalization;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A latitude/longitude position in decimal degrees, WGS-84.
/// </summary>
/// <remarks>
/// Distance and bearing use the haversine formula on a spherical earth. That is accurate to
/// roughly 0.3% — far more than enough to decide whether an aircraft is near a stand, on a
/// runway, or inside a sector, which is the only thing StormATC uses geometry for. A geodesic
/// solution would add precision the simulation does not need.
/// </remarks>
public readonly record struct GeoPosition
{
    /// <summary>Mean earth radius in nautical miles.</summary>
    private const double EarthRadiusNauticalMiles = 3440.065;

    private GeoPosition(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Latitude in decimal degrees, −90 to +90.</summary>
    public double Latitude { get; }

    /// <summary>Longitude in decimal degrees, −180 to +180.</summary>
    public double Longitude { get; }

    /// <summary>Creates a position.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is out of range or not finite.</exception>
    public static GeoPosition Create(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude), latitude, "Latitude must be a finite value between -90 and 90.");
        }

        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitude), longitude, "Longitude must be a finite value between -180 and 180.");
        }

        return new GeoPosition(latitude, longitude);
    }

    /// <summary>
    /// Attempts to create a position. Used on the simulator ingest path, where a momentarily
    /// invalid frame should be dropped rather than crash the connector.
    /// </summary>
    public static bool TryCreate(double latitude, double longitude, out GeoPosition position)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90
            || !double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            position = default;
            return false;
        }

        position = new GeoPosition(latitude, longitude);
        return true;
    }

    /// <summary>Great-circle distance to another position, in nautical miles.</summary>
    public double DistanceNauticalMilesTo(GeoPosition other)
    {
        double lat1 = DegreesToRadians(Latitude);
        double lat2 = DegreesToRadians(other.Latitude);
        double deltaLat = DegreesToRadians(other.Latitude - Latitude);
        double deltaLon = DegreesToRadians(other.Longitude - Longitude);

        double a = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));

        return EarthRadiusNauticalMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Distance to another position in metres.</summary>
    public double DistanceMetersTo(GeoPosition other) =>
        DistanceNauticalMilesTo(other) * 1852.0;

    /// <summary>Initial true bearing to another position, in degrees.</summary>
    public Heading TrueBearingTo(GeoPosition other)
    {
        double lat1 = DegreesToRadians(Latitude);
        double lat2 = DegreesToRadians(other.Latitude);
        double deltaLon = DegreesToRadians(other.Longitude - Longitude);

        double y = Math.Sin(deltaLon) * Math.Cos(lat2);
        double x = (Math.Cos(lat1) * Math.Sin(lat2))
            - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon));

        double bearing = RadiansToDegrees(Math.Atan2(y, x));
        return Heading.FromDegrees(bearing);
    }

    /// <summary>
    /// Projects a new position a given distance and true bearing away. Used by the mock
    /// simulator to advance the aircraft along a track.
    /// </summary>
    public GeoPosition Project(Heading trueBearing, double distanceNauticalMiles)
    {
        double angularDistance = distanceNauticalMiles / EarthRadiusNauticalMiles;
        double bearing = DegreesToRadians(trueBearing.Degrees);
        double lat1 = DegreesToRadians(Latitude);
        double lon1 = DegreesToRadians(Longitude);

        double lat2 = Math.Asin(
            (Math.Sin(lat1) * Math.Cos(angularDistance))
            + (Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing)));

        double lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1),
            Math.Cos(angularDistance) - (Math.Sin(lat1) * Math.Sin(lat2)));

        // Wrap longitude back into -180..180.
        double degreesLon = ((RadiansToDegrees(lon2) + 540) % 360) - 180;
        return Create(RadiansToDegrees(lat2), degreesLon);
    }

    /// <summary>True when this position lies within the given radius of another.</summary>
    public bool IsWithin(GeoPosition other, double radiusNauticalMiles) =>
        DistanceNauticalMilesTo(other) <= radiusNauticalMiles;

    /// <summary>Renders as a decimal-degree pair for display and diagnostics.</summary>
    public string ToDisplay() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude:F5}, {Longitude:F5}");

    /// <inheritdoc />
    public override string ToString() => ToDisplay();

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}
