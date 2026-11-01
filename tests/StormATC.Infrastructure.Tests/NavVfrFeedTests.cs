using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Application.Panel;
using StormATC.Infrastructure.Panel;

namespace StormATC.Infrastructure.Tests;

/// <summary>The map tab's source: NavVFR's local feed, read by StormATC and relayed to the panel.</summary>
public sealed class NavVfrFeedTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-navvfr-" + Guid.NewGuid().ToString("N"));

    public NavVfrFeedTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class FixedState : IToolbarPanelStateSource
    {
        public ToolbarPanelState Current() => new(
            "fr", "F-GABC", "Tour", "118.600", "VFR", new ToolbarPanelPhase("Departure", "Point d'attente"),
            new ToolbarPanelLabels("Dialogue ATC", "Phraséologie", "Vous", "Contrôleur", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], new ToolbarPanelPhraseology("Departure", "Point d'attente", [], [], []));
    }

    private sealed class FakeFeed(byte[]? state, byte[]? map) : INavVfrFeed
    {
        public (double MinLat, double MinLon, double MaxLat, double MaxLon)? LastRectangle { get; private set; }

        public Task<NavVfrTile?> GetTileAsync(int layer, int zoom, int x, int y, CancellationToken cancellationToken) =>
            Task.FromResult<NavVfrTile?>(layer == 0 && zoom == 5 && x == 16 && y == 11 ? new NavVfrTile([1, 2, 3], "image/png") : null);

        public Task<byte[]?> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task<byte[]?> GetMapAsync(double a, double b, double c, double d, CancellationToken cancellationToken)
        {
            LastRectangle = (a, b, c, d);
            return Task.FromResult(map);
        }
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartAsync(INavVfrFeed? feed)
    {
        int port = FreePort();
        var server = new ToolbarPanelServer(
            new FixedState(),
            Options.Create(new ToolbarPanelOptions { Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder,
            navVfr: feed);
        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task The_panel_gets_the_route_and_the_aircraft_as_NavVFR_sent_them()
    {
        byte[] state = Encoding.UTF8.GetBytes("{\"route\":[],\"aircraft\":null}");
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeFeed(state, null));
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/navvfr/state");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(state, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task The_rectangle_of_the_map_is_passed_on_in_degrees()
    {
        var feed = new FakeFeed(null, Encoding.UTF8.GetBytes("{\"airspaces\":[]}"));
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(feed);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/navvfr/map?minLat=47.5&minLon=1.25&maxLat=49&maxLon=3");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal((47.5, 1.25, 49.0, 3.0), feed.LastRectangle);
        }
    }

    [Theory]
    [InlineData("api/navvfr/map")]
    [InlineData("api/navvfr/map?minLat=a&minLon=1&maxLat=2&maxLon=3")]
    [InlineData("api/navvfr/map?minLat=50&minLon=1&maxLat=49&maxLon=3")]
    public async Task A_rectangle_that_is_missing_or_upside_down_is_refused(string path)
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeFeed(null, new byte[] { 1 }));
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("api/navvfr/state")]
    [InlineData("api/navvfr/map?minLat=1&minLon=1&maxLat=2&maxLon=2")]
    public async Task The_panel_is_told_when_NavVFR_is_not_running(string path)
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeFeed(null, null));
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_tile_of_the_base_map_is_relayed_with_its_type_and_an_unknown_one_is_not_found()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(new FakeFeed(null, null));
        using (server)
        using (client)
        {
            using HttpResponseMessage found = await client.GetAsync("api/navvfr/tile/0/5/16/11");
            using HttpResponseMessage missing = await client.GetAsync("api/navvfr/tile/0/5/16/12");
            using HttpResponseMessage junk = await client.GetAsync("api/navvfr/tile/a/b/c/d");

            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await found.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, junk.StatusCode);
        }
    }

    [Fact]
    public async Task Without_a_source_the_map_is_unavailable()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(null);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/navvfr/state");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_client_reads_NavVFR_on_the_loopback_port_and_gives_up_quietly_when_it_is_closed()
    {
        int port = FreePort();
        using var http = new HttpClient();
        var client = new NavVfrFeedClient(
            http, Options.Create(new ToolbarPanelOptions { NavVfrPort = port }), NullLogger<NavVfrFeedClient>.Instance);

        Assert.Null(await client.GetStateAsync(CancellationToken.None));

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task<HttpListenerContext> pending = listener.GetContextAsync();
        Task<byte[]?> read = client.GetMapAsync(47.5, 1.25, 49, 3, CancellationToken.None);
        HttpListenerContext context = await pending;
        string query = context.Request.Url!.PathAndQuery;
        byte[] answer = Encoding.UTF8.GetBytes("{\"ok\":true}");
        context.Response.OutputStream.Write(answer);
        context.Response.Close();

        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString((await read)!));
        Assert.Equal("/api/map?minLat=47.5&minLon=1.25&maxLat=49&maxLon=3", query);
    }
}
