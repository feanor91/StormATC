using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// Supplies nearby simulator traffic and resolves runway occupancy against live airport geometry.
/// </summary>
public sealed class MsfsTrafficProvider(
    MsfsSimConnectConnector connector,
    Mocks.MockTrafficProvider fallback,
    Microsoft.Extensions.Options.IOptionsMonitor<TrafficOptions> options) : ITrafficProvider
{
    private const double RelevantRadiusNauticalMiles = 15;
    private const double RunwayEdgeToleranceMeters = 25;

    private readonly MsfsSimConnectConnector _connector =
        connector ?? throw new ArgumentNullException(nameof(connector));
    private readonly Mocks.MockTrafficProvider _fallback =
        fallback ?? throw new ArgumentNullException(nameof(fallback));
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<TrafficOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public Task<IReadOnlyList<TrafficContact>> GetRelevantTrafficAsync(
        AircraftState aircraftState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_connector.ConnectionState != SimulatorConnectionState.Connected)
        {
            return _fallback.GetRelevantTrafficAsync(aircraftState, cancellationToken);
        }

        if (!_options.CurrentValue.EnableLiveTraffic)
        {
            return Task.FromResult<IReadOnlyList<TrafficContact>>([]);
        }

        IReadOnlyList<AirportData> airports = _connector.GetCachedAirports();
        IReadOnlyList<TrafficContact> contacts =
        [
            .. _connector.NearbyTraffic
                .Where(contact =>
                    contact.Position.DistanceNauticalMilesTo(aircraftState.Position)
                    <= RelevantRadiusNauticalMiles)
                .GroupBy(contact => contact.ObjectId)
                .Select(group => group.First())
                .Select(contact => ToDomainContact(contact, airports)),
        ];

        return Task.FromResult(contacts);
    }

    private static TrafficContact ToDomainContact(
        MsfsTrafficSnapshot snapshot,
        IReadOnlyList<AirportData> airports)
    {
        RunwayIdentifier? runway = FindOccupiedRunway(snapshot, airports);
        return new TrafficContact
        {
            Callsign = snapshot.Callsign,
            Position = snapshot.Position,
            AltitudeFeet = snapshot.AltitudeFeet,
            GroundSpeedKnots = snapshot.GroundSpeedKnots,
            IsOnRunway = snapshot.IsOnAnyRunway || runway is not null,
            OccupiedRunway = runway,
        };
    }

    /// <summary>Finds a runway whose physical rectangle contains the traffic contact.</summary>
    internal static RunwayIdentifier? FindOccupiedRunway(
        MsfsTrafficSnapshot snapshot,
        IReadOnlyList<AirportData> airports)
    {
        if (!snapshot.IsOnGround)
        {
            return null;
        }

        RunwayData? bestRunway = null;
        double bestDistance = double.MaxValue;

        foreach (AirportData airport in airports)
        {
            if (!snapshot.Position.IsWithin(airport.ReferencePosition, 10))
            {
                continue;
            }

            foreach (RunwayData runway in airport.Runways)
            {
                double distance = DistanceToRunwayCenterlineMeters(snapshot.Position, runway);
                double halfWidth = Math.Max(15, runway.WidthFeet * 0.3048 / 2);
                if (distance <= halfWidth + RunwayEdgeToleranceMeters
                    && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestRunway = runway;
                }
            }
        }

        return bestRunway?.Identifier;
    }

    private static double DistanceToRunwayCenterlineMeters(
        GeoPosition point,
        RunwayData runway)
    {
        GeoPosition origin = runway.ThresholdPosition;
        (double px, double py) = ToLocalMeters(origin, point);
        (double ex, double ey) = ToLocalMeters(origin, runway.EndPosition);

        double lengthSquared = (ex * ex) + (ey * ey);
        if (lengthSquared < 1)
        {
            return Math.Sqrt((px * px) + (py * py));
        }

        double projection = Math.Clamp(((px * ex) + (py * ey)) / lengthSquared, 0, 1);
        double dx = px - (projection * ex);
        double dy = py - (projection * ey);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double X, double Y) ToLocalMeters(
        GeoPosition origin,
        GeoPosition position)
    {
        const double metersPerDegreeLatitude = 111_320;
        double latitudeRadians = origin.Latitude * Math.PI / 180;
        double x = (position.Longitude - origin.Longitude)
            * metersPerDegreeLatitude
            * Math.Cos(latitudeRadians);
        double y = (position.Latitude - origin.Latitude) * metersPerDegreeLatitude;
        return (x, y);
    }
}
