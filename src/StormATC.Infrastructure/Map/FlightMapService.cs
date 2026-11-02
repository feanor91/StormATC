using StormATC.Application.Map;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;

namespace StormATC.Infrastructure.Map;

/// <summary>
/// What the panel's map tab shows: the flight plan of the session, the aircraft the simulator reports, and the map data
/// around them. It depends on nothing outside StormATC.
/// </summary>
public sealed class FlightMapService : IMapService
{
    /// <summary>The version of the state format.</summary>
    public const int ProtocolVersion = 2;

    /// <summary>The cruise altitude when there is no plan, in feet.</summary>
    internal const double DefaultCruiseAltitudeFeet = 2500;

    /// <summary>An aircraft not seen for this long is not shown as live.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    private readonly Func<AtcSession> _session;
    private readonly MapDataStore _store;
    private readonly MapTileService _tiles;
    private readonly TimeProvider _clock;
    private int _warmedUp;

    /// <summary>Creates the service.</summary>
    /// <param name="session">The running session, which holds the flight plan and the aircraft.</param>
    /// <param name="store">The airspaces, aerodromes and beacons.</param>
    /// <param name="tiles">The base map tiles.</param>
    /// <param name="clock">The clock.</param>
    public FlightMapService(AtcSessionContext session, MapDataStore store, MapTileService tiles, TimeProvider clock)
        : this(Current(session), store, tiles, clock)
    {
    }

    /// <summary>Creates the service over any source of the current session (the tests').</summary>
    /// <param name="session">The current session.</param>
    /// <param name="store">The airspaces, aerodromes and beacons.</param>
    /// <param name="tiles">The base map tiles.</param>
    /// <param name="clock">The clock.</param>
    internal FlightMapService(Func<AtcSession> session, MapDataStore store, MapTileService tiles, TimeProvider clock)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    private static Func<AtcSession> Current(AtcSessionContext session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return () => session.CurrentSession;
    }

    /// <inheritdoc />
    public MapState GetState()
    {
        if (Interlocked.Exchange(ref _warmedUp, 1) == 0)
        {
            _store.WarmUp();
        }

        _ = _store.RefreshKeysAsync(CancellationToken.None); // the CARTO key reaches the base map within a second
        AtcSession session = _session();
        FlightPlan? plan = session.FlightPlan;
        MapAircraft? aircraft = null;
        long age = -1;
        AircraftState state = session.AircraftState;
        if (!state.InSimulatorMenus && !IsOrigin(state))
        {
            age = (long)Math.Max(0, (_clock.GetUtcNow() - state.Timestamp).TotalMilliseconds);
            aircraft = new MapAircraft(
                state.Position.Latitude,
                state.Position.Longitude,
                state.IndicatedAltitudeFeet,
                state.TrueHeadingDegrees,
                state.GroundSpeedKnots,
                state.IsOnGround);
            if (age > Stale.TotalMilliseconds)
            {
                age = (long)Stale.TotalMilliseconds;
            }
        }

        return new MapState(
            ProtocolVersion,
            Route(plan),
            aircraft,
            plan?.CruiseAltitude?.Feet is { } cruise and > 0 ? cruise : DefaultCruiseAltitudeFeet,
            age,
            _tiles.BaseMap(),
            plan?.DepartureIcao,
            plan?.DestinationIcao,
            _tiles.Styles());
    }

    /// <inheritdoc />
    public async Task<MapData> GetDataAsync(
        double minLatitude,
        double minLongitude,
        double maxLatitude,
        double maxLongitude,
        CancellationToken cancellationToken)
    {
        await _store.RefreshKeysAsync(cancellationToken).ConfigureAwait(false);
        return await _store.QueryAsync(minLatitude, minLongitude, maxLatitude, maxLongitude, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MapTile?> GetTileAsync(string? style, int layer, int zoom, int x, int y, CancellationToken cancellationToken) =>
        _tiles.GetAsync(style, layer, zoom, x, y, cancellationToken);

    /// <summary>
    /// The route of a plan: its departure, the fixes that have a position, its destination. The aerodromes come from the
    /// aerodromes of the country when the plan does not give their position.
    /// </summary>
    /// <param name="plan">The plan, if any.</param>
    private List<MapWaypoint> Route(FlightPlan? plan)
    {
        var route = new List<MapWaypoint>();
        if (plan is null)
        {
            return route;
        }

        string departure = plan.DepartureIcao.ToUpperInvariant();
        string destination = plan.DestinationIcao.ToUpperInvariant();
        Add(route, AirportPoint(departure));

        foreach (RouteLeg leg in plan.RouteLegs)
        {
            if (leg.Position is not { } position)
            {
                continue;
            }

            string name = leg.Identifier.ToUpperInvariant();
            bool aerodrome = name == departure || name == destination;
            Add(route, new MapWaypoint(leg.Identifier, aerodrome ? name : null, position.Latitude, position.Longitude));
        }

        Add(route, AirportPoint(destination));
        return route;
    }

    private MapWaypoint? AirportPoint(string icao) =>
        _store.FindAirport(icao) is { } airport ? new MapWaypoint(icao, icao, airport.Lat, airport.Lon) : null;

    /// <summary>Adds a point, unless the route already ends on the same spot or the same aerodrome.</summary>
    private static void Add(List<MapWaypoint> route, MapWaypoint? point)
    {
        if (point is null)
        {
            return;
        }

        if (route.Count > 0)
        {
            MapWaypoint last = route[^1];
            bool sameSpot = Math.Abs(last.Lat - point.Lat) < 0.0005 && Math.Abs(last.Lon - point.Lon) < 0.0005;
            bool sameAerodrome = point.Icao is not null && string.Equals(last.Icao, point.Icao, StringComparison.OrdinalIgnoreCase);
            if (sameSpot || sameAerodrome)
            {
                if (sameAerodrome && last.Icao is null)
                {
                    route[^1] = point;
                }

                return;
            }
        }

        // An aerodrome the plan names again later in its legs (the departure printed as the first leg) is not drawn twice.
        if (point.Icao is not null && route.Any(p => string.Equals(p.Icao, point.Icao, StringComparison.OrdinalIgnoreCase)) && route.Count > 0
            && route[0].Icao == point.Icao)
        {
            return;
        }

        route.Add(point);
    }

    /// <summary>The simulator reports (0, 0) before it knows where the aircraft is.</summary>
    private static bool IsOrigin(AircraftState state) =>
        Math.Abs(state.Position.Latitude) < 1e-6 && Math.Abs(state.Position.Longitude) < 1e-6;
}
