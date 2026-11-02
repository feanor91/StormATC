using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Map;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Map;

/// <summary>
/// The data behind the panel's map: the airspaces (OpenAIP, needs the pilot's key), the aerodromes and the beacons
/// (OurAirports). Each source is downloaded once and kept in StormATC's data folder, then refreshed after a month.
/// </summary>
public sealed class MapDataStore : IDisposable
{
    /// <summary>The largest number of airspaces and of aerodromes in one answer: the panel must stay fluent.</summary>
    internal const int MaximumAirspaces = 400;

    /// <summary>The largest number of aerodromes or beacons in one answer.</summary>
    internal const int MaximumPoints = 150;

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeyRefresh = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IStoragePaths _paths;
    private readonly ISecretStore _secrets;
    private readonly IOptionsMonitor<MapOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MapDataStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile IReadOnlyList<MapAirport> _airports = [];
    private volatile IReadOnlyList<MapNavaid> _navaids = [];
    private volatile IReadOnlyList<Indexed> _airspaces = [];
    private volatile Dictionary<string, MapAirport> _byIdent = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _cartoKey = string.Empty;
    private string _loadedCountry = string.Empty;
    private bool _basicLoaded;
    private bool _airspacesLoaded;
    private DateTimeOffset _lastAirspaceAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastKeyRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBasicAttempt = DateTimeOffset.MinValue;

    /// <summary>Creates the store.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="paths">Where StormATC keeps its data.</param>
    /// <param name="secrets">The secret store, which holds the OpenAIP and CARTO keys.</param>
    /// <param name="options">The map options.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public MapDataStore(
        HttpClient http,
        IStoragePaths paths,
        ISecretStore secrets,
        IOptionsMonitor<MapOptions> options,
        TimeProvider clock,
        ILogger<MapDataStore> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The CARTO key as last read; empty when there is none.</summary>
    public string CartoKey => _cartoKey;

    /// <summary>The position of an aerodrome of the country, when the aerodromes are loaded.</summary>
    /// <param name="icao">The ICAO code.</param>
    public MapAirport? FindAirport(string icao) => _byIdent.TryGetValue(icao, out MapAirport? airport) ? airport : null;

    /// <summary>Starts loading the aerodromes in the background, so that the first route finds its aerodromes.</summary>
    public void WarmUp() => _ = Task.Run(() => EnsureBasicAsync(CancellationToken.None));

    /// <summary>Reads the keys again, at most every few seconds.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefreshKeysAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (now - _lastKeyRead < KeyRefresh)
        {
            return;
        }

        _lastKeyRead = now;
        try
        {
            _cartoKey = (await _secrets.GetSecretAsync(SecretKeys.CartoApiKey, cancellationToken).ConfigureAwait(false))?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogDebug(ex, "The CARTO key could not be read.");
        }
    }

    /// <summary>The airspaces, aerodromes and beacons inside a rectangle.</summary>
    /// <param name="minLat">South edge.</param>
    /// <param name="minLon">West edge.</param>
    /// <param name="maxLat">North edge.</param>
    /// <param name="maxLon">East edge.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MapData> QueryAsync(double minLat, double minLon, double maxLat, double maxLon, CancellationToken cancellationToken)
    {
        await EnsureBasicAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAirspacesAsync(cancellationToken).ConfigureAwait(false);

        bool truncated = false;
        var zones = new List<MapAirspace>();
        double floor = _options.CurrentValue.MaxAirspaceFloorFeet;
        foreach (Indexed zone in _airspaces)
        {
            if (zone.MaxLat < minLat || zone.MinLat > maxLat || zone.MaxLon < minLon || zone.MinLon > maxLon)
            {
                continue;
            }

            if (!OpenAipAirspaces.IsShown(zone.Airspace, floor > 0 ? floor : null))
            {
                continue;
            }

            if (zones.Count >= MaximumAirspaces)
            {
                truncated = true;
                break;
            }

            zones.Add(zone.Airspace);
        }

        // The important families first: the panel writes a label for the first ones.
        zones.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));

        var airports = new List<MapAirport>();
        foreach (MapAirport airport in _airports)
        {
            if (airport.Lat < minLat || airport.Lat > maxLat || airport.Lon < minLon || airport.Lon > maxLon)
            {
                continue;
            }

            if (airports.Count >= MaximumPoints)
            {
                truncated = true;
                break;
            }

            airports.Add(airport);
        }

        var navaids = new List<MapNavaid>();
        foreach (MapNavaid navaid in _navaids)
        {
            if (navaid.Lat < minLat || navaid.Lat > maxLat || navaid.Lon < minLon || navaid.Lon > maxLon)
            {
                continue;
            }

            if (navaids.Count >= MaximumPoints)
            {
                truncated = true;
                break;
            }

            navaids.Add(navaid);
        }

        return new MapData(zones, airports, navaids, _airspacesLoaded, truncated);
    }

    private static int Rank(string kind) => kind switch
    {
        "restricted" => 0,
        "ctr" => 1,
        "tma" => 2,
        "radio" => 3,
        "leisure" => 4,
        _ => 5,
    };

    private string Folder()
    {
        string folder = Path.Combine(_paths.ApplicationDataDirectory, "map");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task EnsureBasicAsync(CancellationToken cancellationToken)
    {
        string country = _options.CurrentValue.Country.Trim().ToUpperInvariant();
        if (_basicLoaded && country == _loadedCountry)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_basicLoaded && country == _loadedCountry)
            {
                return;
            }

            if (_clock.GetUtcNow() - _lastBasicAttempt < RetryAfter && country == _loadedCountry)
            {
                return;
            }

            _lastBasicAttempt = _clock.GetUtcNow();
            if (country != _loadedCountry)
            {
                _airspacesLoaded = false;
                _airspaces = [];
            }

            List<MapAirport>? airports = await LoadCachedOrDownloadAsync(
                $"airports-{country}.json",
                OurAirportsData.AirportsUrl,
                reader => OurAirportsData.ParseAirports(reader, country),
                cancellationToken).ConfigureAwait(false);
            List<MapNavaid>? navaids = await LoadCachedOrDownloadAsync(
                $"navaids-{country}.json",
                OurAirportsData.NavaidsUrl,
                reader => OurAirportsData.ParseNavaids(reader, country),
                cancellationToken).ConfigureAwait(false);

            if (airports is not null)
            {
                _airports = airports;
                _byIdent = airports.GroupBy(a => a.Ident, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }

            if (navaids is not null)
            {
                _navaids = navaids;
            }

            _loadedCountry = country;
            _basicLoaded = airports is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The kept copy when it is recent, else a new download (the old copy if the download fails).</summary>
    private async Task<List<T>?> LoadCachedOrDownloadAsync<T>(
        string fileName,
        string url,
        Func<TextReader, List<T>> parse,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Folder(), fileName);
        bool exists = File.Exists(path);
        if (exists && _clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) < RefreshAfter)
        {
            List<T>? kept = await ReadCacheAsync<T>(path, cancellationToken).ConfigureAwait(false);
            if (kept is not null)
            {
                return kept;
            }
        }

        try
        {
            await using Stream stream = await _http.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            List<T> fresh = parse(reader);
            await WriteCacheAsync(path, fresh, cancellationToken).ConfigureAwait(false);
            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogInformation(ex, "The map data {File} could not be downloaded.", fileName);
            return exists ? await ReadCacheAsync<T>(path, cancellationToken).ConfigureAwait(false) : null;
        }
    }

    private async Task EnsureAirspacesAsync(CancellationToken cancellationToken)
    {
        if (_airspacesLoaded)
        {
            return;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (now - _lastAirspaceAttempt < RetryAfter)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_airspacesLoaded || _clock.GetUtcNow() - _lastAirspaceAttempt < RetryAfter)
            {
                return;
            }

            _lastAirspaceAttempt = _clock.GetUtcNow();
            string country = _options.CurrentValue.Country.Trim().ToUpperInvariant();
            string path = Path.Combine(Folder(), $"airspaces-{country}.json");
            string? key = null;
            try
            {
                key = (await _secrets.GetSecretAsync(SecretKeys.OpenAipApiKey, cancellationToken).ConfigureAwait(false))?.Trim();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                _logger.LogDebug(ex, "The OpenAIP key could not be read.");
            }

            bool exists = File.Exists(path);
            bool recent = exists && _clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) < RefreshAfter;
            List<MapAirspace>? airspaces = null;
            if (recent || (exists && string.IsNullOrEmpty(key)))
            {
                airspaces = await ReadCacheAsync<MapAirspace>(path, cancellationToken).ConfigureAwait(false);
            }

            if (airspaces is null && !string.IsNullOrEmpty(key))
            {
                airspaces = await DownloadAirspacesAsync(country, key, cancellationToken).ConfigureAwait(false);
                if (airspaces is not null)
                {
                    await WriteCacheAsync(path, airspaces, cancellationToken).ConfigureAwait(false);
                }
                else if (exists)
                {
                    airspaces = await ReadCacheAsync<MapAirspace>(path, cancellationToken).ConfigureAwait(false);
                }
            }

            if (airspaces is not null)
            {
                _airspaces = [.. airspaces.Select(Index)];
                _airspacesLoaded = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MapAirspace>?> DownloadAirspacesAsync(string country, string key, CancellationToken cancellationToken)
    {
        var all = new List<MapAirspace>();
        int page = 1;
        int totalPages = 1;
        try
        {
            while (page <= totalPages && page <= 30)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri($"https://api.core.openaip.net/api/airspaces?country={Uri.EscapeDataString(country)}&limit=500&page={page}"));
                request.Headers.Add("x-openaip-api-key", key);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                all.AddRange(OpenAipAirspaces.ParsePage(document.RootElement, out int pages));
                totalPages = pages;
                page++;
            }

            return all;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // The key, the network or the service: the map goes on without the airspaces. The message never carries the key.
            _logger.LogInformation("The OpenAIP airspaces could not be downloaded ({Reason}).", ex.GetType().Name);
            return null;
        }
    }

    private async Task<List<T>?> ReadCacheAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogInformation(ex, "The kept map data {File} could not be read.", Path.GetFileName(path));
            return null;
        }
    }

    private async Task WriteCacheAsync<T>(string path, List<T> items, CancellationToken cancellationToken)
    {
        try
        {
            string temporary = path + ".tmp";
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, items, Json, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "The map data {File} could not be kept.", Path.GetFileName(path));
        }
    }

    private static Indexed Index(MapAirspace airspace)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (double[][] ring in airspace.Rings)
        {
            foreach (double[] point in ring)
            {
                minLon = Math.Min(minLon, point[0]);
                maxLon = Math.Max(maxLon, point[0]);
                minLat = Math.Min(minLat, point[1]);
                maxLat = Math.Max(maxLat, point[1]);
            }
        }

        return new Indexed(airspace, minLat, maxLat, minLon, maxLon);
    }

    private sealed record Indexed(MapAirspace Airspace, double MinLat, double MaxLat, double MinLon, double MaxLon);
}
