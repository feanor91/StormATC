using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Map;
using StormATC.Application.Options;
using StormATC.Application.Panel;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Map;
using StormATC.Infrastructure.Panel;

namespace StormATC.Infrastructure.Tests;

/// <summary>The map of the panel: StormATC's own flight plan, aircraft, airspaces and tiles.</summary>
public sealed class MapServiceTests : IDisposable
{
    private const string AirportsCsv =
        "id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,continent,iso_country\n" +
        "1,LFPN,small_airport,Toussus,48.7519,2.1061,538,EU,FR\n" +
        "2,LFFN,small_airport,Brienne,48.35,2.95,300,EU,FR\n";

    private const string NavaidsCsv =
        "id,filename,ident,name,type,frequency_khz,latitude_deg,longitude_deg,elevation_ft,iso_country\n" +
        "1,a,TSU,Toussus,VOR-DME,113500,48.75,2.11,500,FR\n";

    private const string AirspacePage = """
    { "totalPages": 1, "items": [
      { "name": "CTR TOUSSUS", "type": 4, "icaoClass": 3,
        "lowerLimit": { "value": 0, "unit": 1, "referenceDatum": 0 }, "upperLimit": { "value": 2500, "unit": 1, "referenceDatum": 1 },
        "geometry": { "type": "Polygon", "coordinates": [ [ [2.0, 48.6], [2.3, 48.6], [2.3, 48.9], [2.0, 48.9], [2.0, 48.6] ] ] } },
      { "name": "LOIN", "type": 4,
        "geometry": { "type": "Polygon", "coordinates": [ [ [8.0, 40.0], [8.3, 40.0], [8.3, 40.3], [8.0, 40.0] ] ] } }
    ] }
    """;

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-map-" + Guid.NewGuid().ToString("N"));

    public MapServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // The map warms its data up in the background the first time it is asked for the state: let that finish.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }

        Directory.Delete(_folder, recursive: true);
    }

    private sealed class Paths(string root) : IStoragePaths
    {
        public string ApplicationDataDirectory => root;

        public string DatabasePath => Path.Combine(root, "test.db");

        public string LogDirectory => root;

        public string FlightPlanCacheDirectory => root;

        public string PromptDirectory => root;

        public string DataDirectory => root;

        public void EnsureCreated()
        {
        }
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(key, out string? v) ? v : null);

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken)
        {
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken) => Task.FromResult(values.ContainsKey(key));
    }

    private sealed class Internet : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public string? OpenAipKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url.EndsWith("airports.csv", StringComparison.Ordinal))
            {
                return Task.FromResult(Reply(AirportsCsv));
            }

            if (url.EndsWith("navaids.csv", StringComparison.Ordinal))
            {
                return Task.FromResult(Reply(NavaidsCsv));
            }

            if (url.Contains("api.core.openaip.net", StringComparison.Ordinal))
            {
                OpenAipKey = request.Headers.GetValues("x-openaip-api-key").Single();
                return Task.FromResult(Reply(AirspacePage));
            }

            if (url.Contains("World_Light_Gray_Base", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Monitor Options(MapOptions? options = null) => new(options ?? new MapOptions());

    private sealed class Monitor(MapOptions value) : IOptionsMonitor<MapOptions>
    {
        public MapOptions CurrentValue => value;

        public MapOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MapOptions, string?> listener) => null;
    }

    private (MapDataStore Store, MapTileService Tiles, Internet Internet, Dictionary<string, string> Keys) Build(MapOptions? options = null)
    {
        var internet = new Internet();
        var keys = new Dictionary<string, string>();
        var http = new HttpClient(internet);
        var store = new MapDataStore(http, new Paths(_folder), new Secrets(keys), Options(options), new FixedTime(Now), NullLogger<MapDataStore>.Instance);
        var tiles = new MapTileService(new HttpClient(internet), new Paths(_folder), Options(options), store, NullLogger<MapTileService>.Instance);
        return (store, tiles, internet, keys);
    }

    private static AtcSession Session(FlightPlan? plan, AircraftState? aircraft = null)
    {
        AircraftState state = aircraft ?? AircraftState.Unknown(Now);
        AtcSession session = AtcSession.Create(Guid.NewGuid(), Now, SessionMode.Mock, state);
        return plan is null ? session : session.WithFlightPlan(plan);
    }

    private static FlightPlan Plan(params RouteLeg[] legs) => new()
    {
        FlightPlanId = "p",
        Callsign = Callsign.Create("FHSTM"),
        DepartureIcao = "LFPN",
        DestinationIcao = "LFFN",
        RouteLegs = legs,
        CruiseAltitude = Altitude.FromFeet(3500),
        SourceIdentifier = "Test",
    };

    private static AircraftState Flying(double lat, double lon, DateTimeOffset at, bool menus = false) => AircraftState.Unknown(at) with
    {
        Position = GeoPosition.Create(lat, lon),
        IndicatedAltitudeFeet = 2400,
        GroundSpeedKnots = 105,
        TrueHeadingDegrees = 112,
        IsOnGround = false,
        InSimulatorMenus = menus,
    };

    private static RouteLeg Leg(string id, double lat, double lon) => new() { Identifier = id, Position = GeoPosition.Create(lat, lon) };

    [Fact]
    public async Task The_route_is_the_plan_from_the_departure_to_the_destination_through_its_fixes()
    {
        var (store, tiles, _, _) = Build();
        await store.QueryAsync(0, 0, 90, 90, CancellationToken.None); // loads the aerodromes
        var service = new FlightMapService(
            () => Session(Plan(Leg("LFPN", 48.7519, 2.1061), Leg("WP1", 48.6, 2.5), Leg("LFFN", 48.35, 2.95))), store, tiles, new FixedTime(Now));

        MapState state = service.GetState();

        Assert.Equal(["LFPN", "WP1", "LFFN"], state.Route.Select(p => p.Name));
        Assert.Equal(["LFPN", null, "LFFN"], state.Route.Select(p => p.Icao));
        Assert.Equal(3500, state.CruiseAltFt);
        Assert.Equal("LFPN", state.Departure);
        Assert.Equal("LFFN", state.Destination);
    }

    [Fact]
    public async Task A_plan_without_positions_still_draws_the_line_between_its_aerodromes()
    {
        var (store, tiles, _, _) = Build();
        await store.QueryAsync(0, 0, 90, 90, CancellationToken.None);
        var service = new FlightMapService(() => Session(Plan()), store, tiles, new FixedTime(Now));

        MapState state = service.GetState();

        Assert.Equal(["LFPN", "LFFN"], state.Route.Select(p => p.Name));
        Assert.Equal(48.7519, state.Route[0].Lat);
    }

    [Fact]
    public void Without_a_plan_there_is_no_route_and_the_cruise_altitude_is_the_default()
    {
        var (store, tiles, _, _) = Build();
        var service = new FlightMapService(() => Session(null), store, tiles, new FixedTime(Now));

        MapState state = service.GetState();

        Assert.Empty(state.Route);
        Assert.Equal(FlightMapService.DefaultCruiseAltitudeFeet, state.CruiseAltFt);
        Assert.Null(state.Aircraft);
        Assert.Equal(-1, state.AircraftAgeMs);
    }

    [Fact]
    public void The_aircraft_is_shown_with_its_age_and_hidden_in_the_menus_or_at_the_origin()
    {
        var (store, tiles, _, _) = Build();
        MapState At(AircraftState aircraft) => new FlightMapService(() => Session(null, aircraft), store, tiles, new FixedTime(Now)).GetState();

        MapState flying = At(Flying(48.7, 2.1, Now.AddSeconds(-2)));
        Assert.NotNull(flying.Aircraft);
        Assert.Equal(48.7, flying.Aircraft!.Lat);
        Assert.Equal(112, flying.Aircraft.HeadingTrue);
        Assert.Equal(2000, flying.AircraftAgeMs);

        Assert.Null(At(Flying(48.7, 2.1, Now, menus: true)).Aircraft);
        Assert.Null(At(AircraftState.Unknown(Now)).Aircraft);
    }

    [Fact]
    public async Task The_data_of_a_rectangle_holds_what_lies_inside_it()
    {
        var (store, _, internet, keys) = Build();
        keys[SecretKeys.OpenAipApiKey] = "SECRET";

        MapData data = await store.QueryAsync(48, 1.5, 49, 3.5, CancellationToken.None);

        Assert.Equal(["CTR TOUSSUS"], data.Airspaces.Select(z => z.Name));
        Assert.Equal(["LFPN", "LFFN"], data.Airports.Select(a => a.Ident));
        Assert.Equal(["TSU"], data.Navaids.Select(n => n.Ident));
        Assert.True(data.AirspacesLoaded);
        Assert.Equal("SECRET", internet.OpenAipKey);
    }

    [Fact]
    public async Task Without_a_key_the_map_says_the_airspaces_are_not_loaded_and_never_calls_OpenAIP()
    {
        var (store, _, internet, _) = Build();

        MapData data = await store.QueryAsync(48, 1.5, 49, 3.5, CancellationToken.None);

        Assert.False(data.AirspacesLoaded);
        Assert.Empty(data.Airspaces);
        Assert.Equal(2, data.Airports.Count);
        Assert.DoesNotContain(internet.Requests, r => r.Contains("openaip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_was_downloaded_is_kept_and_not_downloaded_again()
    {
        var (store, _, internet, keys) = Build();
        keys[SecretKeys.OpenAipApiKey] = "SECRET";
        await store.QueryAsync(48, 1.5, 49, 3.5, CancellationToken.None);
        int first = internet.Requests.Count;

        var (second, _, internetAgain, keysAgain) = Build();
        keysAgain[SecretKeys.OpenAipApiKey] = "SECRET";
        MapData data = await second.QueryAsync(48, 1.5, 49, 3.5, CancellationToken.None);

        Assert.Equal(3, first);
        Assert.Empty(internetAgain.Requests);
        Assert.Single(data.Airspaces);
        Assert.Equal(2, data.Airports.Count);
    }

    [Fact]
    public async Task A_tile_is_downloaded_once_then_kept_on_disk()
    {
        var (_, tiles, internet, _) = Build();

        MapTile? first = await tiles.GetAsync(0, 8, 129, 90, CancellationToken.None);
        MapTile? again = await tiles.GetAsync(0, 8, 129, 90, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("image/png", first!.ContentType);
        Assert.Same(first, again);
        Assert.Single(internet.Requests);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "map-tiles"), "*.tile", SearchOption.AllDirectories));

        (_, MapTileService fresh, Internet offline, _) = Build();
        Assert.NotNull(await fresh.GetAsync(0, 8, 129, 90, CancellationToken.None));
        Assert.Empty(offline.Requests);
    }

    [Fact]
    public async Task A_tile_that_does_not_exist_is_null()
    {
        var (_, tiles, internet, _) = Build();

        Assert.Null(await tiles.GetAsync(0, 40, 0, 0, CancellationToken.None));
        Assert.Null(await tiles.GetAsync(5, 5, 0, 0, CancellationToken.None));
        Assert.Empty(internet.Requests);
    }

    [Fact]
    public void The_base_map_is_the_one_of_the_options_with_a_fall_back_without_the_carto_key()
    {
        var (_, tiles, _, _) = Build(new MapOptions { BaseMap = "carto-light" });

        MapBaseMap map = tiles.BaseMap();

        Assert.Equal("clair", map.Key);
        Assert.Equal(2, map.Layers);
        Assert.Equal(16, map.MaxZoom);
    }

    // ---- the server ----

    private sealed class FakeMap : IMapService
    {
        public (double, double, double, double)? Rectangle { get; private set; }

        public MapState GetState() => new(2, [new MapWaypoint("LFPN", "LFPN", 48.75, 2.11)], null, 2500, -1, new MapBaseMap("clair", 2, 16), "LFPN", "LFFN");

        public Task<MapData> GetDataAsync(double a, double b, double c, double d, CancellationToken cancellationToken)
        {
            Rectangle = (a, b, c, d);
            return Task.FromResult(new MapData([], [new MapAirport("LFPN", "Toussus", 48.75, 2.11)], [], true, false));
        }

        public Task<MapTile?> GetTileAsync(int layer, int zoom, int x, int y, CancellationToken cancellationToken) =>
            Task.FromResult<MapTile?>(layer == 0 && zoom == 5 && x == 16 && y == 11 ? new MapTile([1, 2, 3], "image/png") : null);
    }

    private sealed class FixedState : IToolbarPanelStateSource
    {
        public ToolbarPanelState Current() => new(
            "fr", "F-GABC", "Tour", "118.600", "VFR", new ToolbarPanelPhase("Departure", "Point d'attente"),
            new ToolbarPanelLabels("Dialogue ATC", "Phraséologie", "Vous", "Contrôleur", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], new ToolbarPanelPhraseology("Departure", "Point d'attente", [], [], []));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartAsync(IMapService? map)
    {
        int port = FreePort();
        var server = new ToolbarPanelServer(
            new FixedState(),
            Microsoft.Extensions.Options.Options.Create(new ToolbarPanelOptions { Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder,
            map: map);
        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task The_panel_gets_the_state_in_camel_case_json()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeMap());
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/map/state");
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("LFPN", body.RootElement.GetProperty("route")[0].GetProperty("icao").GetString());
            Assert.Equal(2500, body.RootElement.GetProperty("cruiseAltFt").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("baseMap").GetProperty("layers").GetInt32());
            Assert.Equal(-1, body.RootElement.GetProperty("aircraftAgeMs").GetInt32());
        }
    }

    [Fact]
    public async Task The_rectangle_is_passed_on_and_a_bad_one_is_refused()
    {
        var map = new FakeMap();
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(map);
        using (server)
        using (client)
        {
            using HttpResponseMessage good = await client.GetAsync("api/map/data?minLat=47.5&minLon=1.25&maxLat=49&maxLon=3");
            using HttpResponseMessage upsideDown = await client.GetAsync("api/map/data?minLat=50&minLon=1&maxLat=49&maxLon=3");
            using HttpResponseMessage missing = await client.GetAsync("api/map/data");

            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            Assert.Equal((47.5, 1.25, 49.0, 3.0), map.Rectangle);
            Assert.Contains("airspacesLoaded", await good.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, upsideDown.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
    }

    [Fact]
    public async Task A_tile_is_served_with_its_type_and_an_unknown_one_is_not_found()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeMap());
        using (server)
        using (client)
        {
            using HttpResponseMessage found = await client.GetAsync("api/map/tile/0/5/16/11");
            using HttpResponseMessage missing = await client.GetAsync("api/map/tile/0/5/16/12");
            using HttpResponseMessage junk = await client.GetAsync("api/map/tile/a/b/c/d");

            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await found.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, junk.StatusCode);
        }
    }

    [Fact]
    public async Task Without_a_map_the_endpoints_are_unavailable()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(null);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/map/state");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }
}
