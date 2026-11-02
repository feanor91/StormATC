namespace StormATC.Application.Map;

/// <summary>A point of the route drawn on the map.</summary>
/// <param name="Name">Its identifier.</param>
/// <param name="Icao">The ICAO code when the point is an aerodrome.</param>
/// <param name="Lat">Latitude in degrees.</param>
/// <param name="Lon">Longitude in degrees.</param>
public sealed record MapWaypoint(string Name, string? Icao, double Lat, double Lon);

/// <summary>The aircraft as the simulator reports it.</summary>
/// <param name="Lat">Latitude in degrees.</param>
/// <param name="Lon">Longitude in degrees.</param>
/// <param name="AltitudeFt">Indicated altitude in feet.</param>
/// <param name="HeadingTrue">True heading in degrees.</param>
/// <param name="GroundSpeedKt">Ground speed in knots.</param>
/// <param name="OnGround">True when the aircraft is on the ground.</param>
public sealed record MapAircraft(double Lat, double Lon, double AltitudeFt, double HeadingTrue, double GroundSpeedKt, bool OnGround);

/// <summary>The base map the panel draws, tile by tile, through StormATC.</summary>
/// <param name="Key">The map: <c>clair</c>, <c>carto-light</c>, <c>carto-voyager</c>, <c>relief</c> or <c>rues</c>.</param>
/// <param name="Layers">How many tile layers it has (the light grey map has two).</param>
/// <param name="MaxZoom">The deepest tile zoom.</param>
public sealed record MapBaseMap(string Key, int Layers, int MaxZoom);

/// <summary>A base map the pilot can choose in the panel.</summary>
/// <param name="Key">Its key: <c>clair</c>, <c>carto-light</c>, <c>carto-voyager</c>, <c>relief</c> or <c>rues</c>.</param>
/// <param name="Layers">How many tile layers it has.</param>
/// <param name="MaxZoom">Its deepest tile zoom.</param>
/// <param name="Available">False for a CARTO map while no CARTO key is stored.</param>
public sealed record MapStyle(string Key, int Layers, int MaxZoom, bool Available);

/// <summary>What the map shows of the flight right now.</summary>
/// <param name="Version">The version of this format.</param>
/// <param name="Route">The route: departure, the fixes of the plan that have a position, destination.</param>
/// <param name="Aircraft">The aircraft; null when the simulator is not connected.</param>
/// <param name="CruiseAltFt">The planned cruise altitude, or 2500 ft without a plan.</param>
/// <param name="AircraftAgeMs">Milliseconds since the aircraft was last seen, -1 when there is none.</param>
/// <param name="BaseMap">The base map.</param>
/// <param name="Departure">The departure aerodrome of the plan.</param>
/// <param name="Destination">The destination aerodrome of the plan.</param>
/// <param name="Styles">The base maps the pilot can choose from.</param>
public sealed record MapState(
    int Version,
    IReadOnlyList<MapWaypoint> Route,
    MapAircraft? Aircraft,
    double CruiseAltFt,
    long AircraftAgeMs,
    MapBaseMap? BaseMap,
    string? Departure,
    string? Destination,
    IReadOnlyList<MapStyle>? Styles = null);

/// <summary>An airspace: outer rings in (longitude, latitude), limits as printed and in feet.</summary>
/// <param name="Name">Its name.</param>
/// <param name="IcaoClass">Its ICAO class, or an empty string.</param>
/// <param name="Type">Its type in words (CTR, TMA, R...).</param>
/// <param name="Kind">The family that decides the style: restricted, ctr, tma, radio, leisure, sector or other.</param>
/// <param name="Lower">The floor as printed.</param>
/// <param name="Upper">The ceiling as printed.</param>
/// <param name="LowerFt">The floor in feet (the ground counts for 0).</param>
/// <param name="UpperFt">The ceiling in feet.</param>
/// <param name="Rings">The outer rings, each a list of (longitude, latitude).</param>
public sealed record MapAirspace(
    string Name,
    string IcaoClass,
    string Type,
    string Kind,
    string Lower,
    string Upper,
    double LowerFt,
    double UpperFt,
    double[][][] Rings);

/// <summary>An aerodrome of the map.</summary>
/// <param name="Ident">Its identifier.</param>
/// <param name="Name">Its name.</param>
/// <param name="Lat">Latitude in degrees.</param>
/// <param name="Lon">Longitude in degrees.</param>
public sealed record MapAirport(string Ident, string Name, double Lat, double Lon);

/// <summary>A VOR or an NDB.</summary>
/// <param name="Ident">Its identifier.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>vor</c> or <c>ndb</c>.</param>
/// <param name="FrequencyKhz">Its frequency in kHz (113500 for a VOR on 113.5 MHz).</param>
/// <param name="Lat">Latitude in degrees.</param>
/// <param name="Lon">Longitude in degrees.</param>
public sealed record MapNavaid(string Ident, string Name, string Kind, double FrequencyKhz, double Lat, double Lon);

/// <summary>The airspaces, aerodromes and beacons inside a rectangle of the map.</summary>
/// <param name="Airspaces">The airspaces.</param>
/// <param name="Airports">The aerodromes.</param>
/// <param name="Navaids">The beacons.</param>
/// <param name="AirspacesLoaded">False while there is no airspace data (no OpenAIP key yet).</param>
/// <param name="Truncated">True when the answer was cut because the map is crowded.</param>
public sealed record MapData(
    IReadOnlyList<MapAirspace> Airspaces,
    IReadOnlyList<MapAirport> Airports,
    IReadOnlyList<MapNavaid> Navaids,
    bool AirspacesLoaded,
    bool Truncated);

/// <summary>A tile image and its media type.</summary>
/// <param name="Bytes">The image.</param>
/// <param name="ContentType">Its media type.</param>
public sealed record MapTile(byte[] Bytes, string ContentType);

/// <summary>What the panel's map tab asks StormATC for: the flight, the data around it and the base map tiles.</summary>
public interface IMapService
{
    /// <summary>The route, the aircraft and the base map, right now.</summary>
    MapState GetState();

    /// <summary>The airspaces, aerodromes and beacons inside a rectangle.</summary>
    /// <param name="minLatitude">South edge, degrees.</param>
    /// <param name="minLongitude">West edge, degrees.</param>
    /// <param name="maxLatitude">North edge, degrees.</param>
    /// <param name="maxLongitude">East edge, degrees.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MapData> GetDataAsync(
        double minLatitude,
        double minLongitude,
        double maxLatitude,
        double maxLongitude,
        CancellationToken cancellationToken);

    /// <summary>One tile of a base map; null when there is no such tile or it could not be fetched.</summary>
    /// <param name="style">The base map; null for the one of the settings.</param>
    /// <param name="layer">The layer of the base map.</param>
    /// <param name="zoom">The tile zoom.</param>
    /// <param name="x">The tile column.</param>
    /// <param name="y">The tile row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MapTile?> GetTileAsync(string? style, int layer, int zoom, int x, int y, CancellationToken cancellationToken);
}
