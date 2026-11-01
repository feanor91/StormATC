namespace StormATC.Application.Abstractions;

/// <summary>
/// What NavVFR shares for the panel's map tab: its route, the aircraft it tracks and the airspaces around.
/// The answers are NavVFR's own JSON, handed to the panel as they are.
/// </summary>
public interface INavVfrFeed
{
    /// <summary>The route, the aircraft and the cruise altitude; null when NavVFR is not running.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]?> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>One tile of NavVFR's base map; null when NavVFR is not running or has no such tile.</summary>
    /// <param name="layer">The layer of the base map (the light grey map has two).</param>
    /// <param name="zoom">The tile zoom.</param>
    /// <param name="x">The tile column.</param>
    /// <param name="y">The tile row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NavVfrTile?> GetTileAsync(int layer, int zoom, int x, int y, CancellationToken cancellationToken);

    /// <summary>The airspaces and aerodromes inside a rectangle; null when NavVFR is not running.</summary>
    /// <param name="minLatitude">South edge, degrees.</param>
    /// <param name="minLongitude">West edge, degrees.</param>
    /// <param name="maxLatitude">North edge, degrees.</param>
    /// <param name="maxLongitude">East edge, degrees.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]?> GetMapAsync(
        double minLatitude,
        double minLongitude,
        double maxLatitude,
        double maxLongitude,
        CancellationToken cancellationToken);
}

/// <summary>A tile image and its media type.</summary>
/// <param name="Bytes">The image.</param>
/// <param name="ContentType">Its media type.</param>
public sealed record NavVfrTile(byte[] Bytes, string ContentType);
