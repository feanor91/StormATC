using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Map;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Map;

/// <summary>
/// The tiles of the base map. StormATC downloads them (the simulator's panel never reaches the internet), keeps them in
/// memory and in its data folder, and serves them to the panel.
/// </summary>
public sealed class MapTileService : IDisposable
{
    private const int MemoryTiles = 1500;

    private readonly HttpClient _http;
    private readonly IStoragePaths _paths;
    private readonly IOptionsMonitor<MapOptions> _options;
    private readonly MapDataStore _store;
    private readonly ILogger<MapTileService> _logger;
    private readonly ConcurrentDictionary<string, MapTile> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(6, 6);

    /// <summary>Creates the service.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="paths">Where StormATC keeps its data.</param>
    /// <param name="options">The map options.</param>
    /// <param name="store">The map data, which knows the CARTO key.</param>
    /// <param name="logger">The logger.</param>
    public MapTileService(
        HttpClient http,
        IStoragePaths paths,
        IOptionsMonitor<MapOptions> options,
        MapDataStore store,
        ILogger<MapTileService> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <inheritdoc />
    public void Dispose() => _downloads.Dispose();

    /// <summary>The base map shown, with the CARTO key as last read.</summary>
    public MapBaseMap BaseMap()
    {
        string key = _options.CurrentValue.BaseMap;
        string carto = _store.CartoKey;
        return new MapBaseMap(
            MapBaseMaps.Effective(key, carto),
            MapBaseMaps.LayerCount(key, carto),
            MapBaseMaps.MaxZoom(key, carto));
    }

    /// <summary>The base maps the pilot can choose from, each with its layers and its zoom.</summary>
    public IReadOnlyList<MapStyle> Styles()
    {
        string carto = _store.CartoKey;
        return [.. MapBaseMaps.Keys.Select(k => new MapStyle(k, MapBaseMaps.LayerCount(k, carto), MapBaseMaps.MaxZoom(k, carto), MapBaseMaps.IsAvailable(k, carto)))];
    }

    /// <summary>One tile; null when it does not exist or could not be fetched.</summary>
    /// <param name="style">The base map asked for; null for the one of the settings.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="zoom">The zoom.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MapTile?> GetAsync(string? style, int layer, int zoom, int x, int y, CancellationToken cancellationToken)
    {
        await _store.RefreshKeysAsync(cancellationToken).ConfigureAwait(false);
        string carto = _store.CartoKey;
        string key = style ?? _options.CurrentValue.BaseMap;
        if (style is not null && !MapBaseMaps.IsAvailable(style, carto))
        {
            // An unknown map, or a CARTO map without its key: never another map's tile under this name.
            return null;
        }

        string? url = MapBaseMaps.TileUrl(key, carto, layer, zoom, x, y);
        if (url is null)
        {
            return null;
        }

        string effective = MapBaseMaps.Effective(key, carto);
        string id = $"{effective}/{layer}/{zoom}/{x}/{y}";
        if (_memory.TryGetValue(id, out MapTile? hit))
        {
            return hit;
        }

        string file = Path.Combine(_paths.ApplicationDataDirectory, "map-tiles", effective, layer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tile");
        try
        {
            if (File.Exists(file))
            {
                byte[] kept = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                return Remember(id, kept);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "A kept map tile could not be read.");
        }

        await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.UserAgent.ParseAdd("StormATC (flight simulation)");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "A map tile could not be kept.");
            }

            return Remember(id, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "A map tile could not be downloaded.");
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    private MapTile Remember(string id, byte[] bytes)
    {
        var tile = new MapTile(bytes, ContentTypeOf(bytes));
        if (_memory.Count >= MemoryTiles)
        {
            _memory.Clear();
        }

        _memory[id] = tile;
        return tile;
    }

    /// <summary>The media type of an image, read from its first bytes.</summary>
    /// <param name="bytes">The image.</param>
    internal static string ContentTypeOf(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
        : bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
        : "application/octet-stream";
}
