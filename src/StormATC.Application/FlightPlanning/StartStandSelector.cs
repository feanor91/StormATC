using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.FlightPlanning;

/// <summary>Chooses the parking stand a flight starts on when the pilot asked to start at the parking.</summary>
public static class StartStandSelector
{
    /// <summary>How close another aircraft has to be for a stand to count as taken, in metres.</summary>
    public const double OccupiedWithinMeters = 20;

    /// <summary>
    /// Picks the stand: free, big enough for the aircraft, of the kind that fits it (a gate for an airliner,
    /// a ramp for a light aircraft, a military apron on a military base), and the nearest to where the
    /// aircraft stands now.
    /// </summary>
    /// <param name="airport">The departure aerodrome.</param>
    /// <param name="from">Where the aircraft stands now.</param>
    /// <param name="wingSpanMeters">The aircraft's wingspan, or 0 when it is not known.</param>
    /// <param name="occupied">Positions of other aircraft on the ground.</param>
    /// <param name="militaryBase">True when the aerodrome is a military base.</param>
    /// <returns>The stand, or null when the aerodrome has none that fits.</returns>
    public static StandData? Choose(
        AirportData airport,
        GeoPosition from,
        double wingSpanMeters,
        IReadOnlyList<GeoPosition> occupied,
        bool militaryBase)
    {
        ArgumentNullException.ThrowIfNull(airport);
        ArgumentNullException.ThrowIfNull(occupied);

        IEnumerable<StandData> candidates = airport.Stands.Where(stand =>
            stand.ParkingType is not (11 or 12 or 13)
            && stand.HeadingDegrees is not null
            && Fits(stand, wingSpanMeters)
            && !occupied.Any(other => other.DistanceMetersTo(stand.Position) < OccupiedWithinMeters));

        return candidates
            .OrderBy(stand => IsPreferredKind(stand.ParkingType, wingSpanMeters, militaryBase) ? 0 : 1)
            .ThenBy(stand => from.DistanceMetersTo(stand.Position))
            .FirstOrDefault();
    }

    private static bool Fits(StandData stand, double wingSpanMeters) =>
        stand.RadiusMeters <= 0 || wingSpanMeters <= 0 || stand.RadiusMeters * 2 >= wingSpanMeters * 0.9;

    private static bool IsPreferredKind(int parkingType, double wingSpanMeters, bool militaryBase) => militaryBase
        ? parkingType is 6 or 7
        : wingSpanMeters >= 20
            ? parkingType is 4 or 5 or 8 or 9 or 10
            : parkingType is >= 1 and <= 4;
}
