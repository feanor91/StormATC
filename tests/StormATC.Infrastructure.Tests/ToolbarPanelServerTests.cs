using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Application.Panel;
using StormATC.Infrastructure.Panel;

namespace StormATC.Infrastructure.Tests;

/// <summary>The local web server that feeds the in-simulator panel.</summary>
public sealed class ToolbarPanelServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-panel-" + Guid.NewGuid().ToString("N"));

    public ToolbarPanelServerTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "standalone.html"), "<html>standalone</html>");
        File.WriteAllText(Path.Combine(_folder, "panel-core.js"), "var x = 1;");
        File.WriteAllText(Path.Combine(_folder, "secret.txt"), "not served");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FixedState : IToolbarPanelStateSource
    {
        public ToolbarPanelState Current() => new(
            "fr",
            "F-GABC",
            "Tour",
            "118.600",
            "VFR",
            new ToolbarPanelPhase("Departure", "Point d'attente"),
            new ToolbarPanelLabels("Dialogue ATC", "Phraséologie", "Vous", "Contrôleur", "-", "-", "-", "-", "-", "-", "-", "-"),
            [new ToolbarPanelTurn("1", "10:00", "pilot", "Vous", "Bonjour")],
            new ToolbarPanelPhraseology("Departure", "Point d'attente", [], [], []));
    }

    private sealed class FakeVoices(VoiceRejectionOutcome outcome) : IVoiceRejection
    {
        public int Rejections { get; private set; }

        public SpokenVoice? LastVoice => new("fr_FR-mls-medium, speaker 42", "fr_FR-mls-medium:42", true);

        public Task<VoiceRejectionResult> RejectLastVoiceAsync(CancellationToken cancellationToken)
        {
            Rejections++;
            return Task.FromResult(new VoiceRejectionResult(outcome, "fr_FR-mls-medium, speaker 42"));
        }
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartWithVoicesAsync(IVoiceRejection? voices)
    {
        int port = FreePort();
        var server = new ToolbarPanelServer(
            new FixedState(),
            Options.Create(new ToolbarPanelOptions { Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder,
            voices);

        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    private static HttpRequestMessage RejectRequest(string? marker = "1", string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/voice/reject") { Content = new StringContent(string.Empty) };
        if (marker is not null)
        {
            request.Headers.Add("X-StormATC", marker);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    [Fact]
    public async Task The_panel_can_set_the_last_voice_aside()
    {
        var voices = new FakeVoices(VoiceRejectionOutcome.SetAside);
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(voices);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RejectRequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("setAside", json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("fr_FR-mls-medium, speaker 42", json.RootElement.GetProperty("voice").GetString());
            Assert.Equal(1, voices.Rejections);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(VoiceRejectionOutcome.ChosenInSettings, "chosenInSettings")]
    [InlineData(VoiceRejectionOutcome.NoVoiceYet, "noVoiceYet")]
    [InlineData(VoiceRejectionOutcome.Failed, "failed")]
    public async Task The_outcome_is_reported_to_the_panel(VoiceRejectionOutcome outcome, string expected)
    {
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(new FakeVoices(outcome));
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RejectRequest());

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expected, json.RootElement.GetProperty("outcome").GetString());
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(null, "http://evil.example")]
    [InlineData("1", "http://evil.example")]
    [InlineData("1", "https://evil.example")]
    [InlineData("0", null)]
    public async Task A_request_from_a_web_page_or_without_the_marker_is_refused(string? marker, string? origin)
    {
        var voices = new FakeVoices(VoiceRejectionOutcome.SetAside);
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(voices);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RejectRequest(marker, origin));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, voices.Rejections);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("coui://html_ui")]
    [InlineData("null")]
    [InlineData("file://")]
    public async Task The_simulators_own_pages_are_trusted(string origin)
    {
        var voices = new FakeVoices(VoiceRejectionOutcome.SetAside);
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(voices);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RejectRequest(origin: origin));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_page_served_by_the_server_itself_is_trusted()
    {
        var voices = new FakeVoices(VoiceRejectionOutcome.SetAside);
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(voices);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(
                RejectRequest(origin: client.BaseAddress!.GetLeftPart(UriPartial.Authority)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_a_voice_service_there_is_nothing_to_reject()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartWithVoicesAsync(null);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RejectRequest());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeRoutes(bool applied) : ISimRouteReceiver
    {
        public List<(string Source, bool Initial, string Route)> Received { get; } = [];

        public Task<bool> ReceiveRouteAsync(string source, bool initial, string routeJson, CancellationToken cancellationToken)
        {
            Received.Add((source, initial, routeJson));
            return Task.FromResult(applied);
        }
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartWithRoutesAsync(ISimRouteReceiver? routes)
    {
        int port = FreePort();
        var server = new ToolbarPanelServer(
            new FixedState(),
            Options.Create(new ToolbarPanelOptions { Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder,
            null,
            routes);

        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    private static HttpRequestMessage RouteRequest(string body, string? marker = "1", string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/flightplan") { Content = new StringContent(body) };
        if (marker is not null)
        {
            request.Headers.Add("X-StormATC", marker);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    private const string RouteBody = """
        { "source": "atc", "initial": false, "route": { "departureAirport": { "ident": "LFPL" }, "isVfr": true } }
        """;

    [Fact]
    public async Task A_route_read_in_the_simulator_reaches_StormATC()
    {
        var routes = new FakeRoutes(applied: true);
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(routes);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RouteRequest(RouteBody));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("applied").GetBoolean());
            (string source, bool initial, string route) = Assert.Single(routes.Received);
            Assert.Equal("atc", source);
            Assert.False(initial);
            Assert.Contains("LFPL", route, StringComparison.Ordinal);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_route_already_there_when_the_panel_started_is_flagged_as_initial()
    {
        var routes = new FakeRoutes(applied: false);
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(routes);
        using (server)
        using (client)
        {
            string body = RouteBody.Replace("\"initial\": false", "\"initial\": true").Replace("\"atc\"", "\"efb\"");

            using HttpResponseMessage response = await client.SendAsync(RouteRequest(body));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            (string source, bool initial, _) = Assert.Single(routes.Received);
            Assert.Equal("efb", source);
            Assert.True(initial);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(null, "http://evil.example")]
    [InlineData("1", "http://evil.example")]
    [InlineData("0", null)]
    public async Task A_route_from_a_web_page_or_without_the_marker_is_refused(string? marker, string? origin)
    {
        var routes = new FakeRoutes(applied: true);
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(routes);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RouteRequest(RouteBody, marker, origin));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(routes.Received);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "source": "atc" }""")]
    [InlineData("""{ "source": "atc", "route": 5 }""")]
    [InlineData("""{ "source": "elsewhere", "route": {} }""")]
    [InlineData("""{ "route": {} }""")]
    public async Task A_malformed_route_is_refused(string body)
    {
        var routes = new FakeRoutes(applied: true);
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(routes);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RouteRequest(body));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(routes.Received);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_oversized_route_is_refused()
    {
        var routes = new FakeRoutes(applied: true);
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(routes);
        using (server)
        using (client)
        {
            string body = "{ \"source\": \"atc\", \"route\": { \"padding\": \"" + new string('x', 600 * 1024) + "\" } }";

            using HttpResponseMessage response = await client.SendAsync(RouteRequest(body));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Empty(routes.Received);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_a_receiver_a_route_has_nowhere_to_go()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartWithRoutesAsync(null);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.SendAsync(RouteRequest(RouteBody));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartAsync(bool enabled = true)
    {
        int port = FreePort();
        var server = new ToolbarPanelServer(
            new FixedState(),
            Options.Create(new ToolbarPanelOptions { Enabled = enabled, Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder);

        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task The_state_is_served_as_json_that_any_origin_may_read()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync();
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/state");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("F-GABC", json.RootElement.GetProperty("callsign").GetString());
            Assert.Equal("Tour", json.RootElement.GetProperty("controller").GetString());
            Assert.Equal("Bonjour", json.RootElement.GetProperty("turns")[0].GetProperty("text").GetString());
            Assert.Equal("Dialogue ATC", json.RootElement.GetProperty("labels").GetProperty("tabDialogue").GetString());

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("panel", "<html>standalone</html>")]
    [InlineData("", "<html>standalone</html>")]
    [InlineData("panel/panel-core.js", "var x = 1;")]
    public async Task The_panel_page_and_its_files_are_served(string path, string expected)
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync();
        using (server)
        using (client)
        {
            Assert.Equal(expected, await client.GetStringAsync(path));
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("panel/secret.txt")]
    [InlineData("panel/..%2Fsecret.txt")]
    [InlineData("panel/%2e%2e/secret.txt")]
    [InlineData("panel/missing.js")]
    [InlineData("elsewhere")]
    public async Task Only_the_panel_files_are_served(string path)
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync();
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_server_is_read_only()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync();
        using (server)
        using (client)
        {
            using HttpResponseMessage post = await client.PostAsync("api/state", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

            using var preflight = new HttpRequestMessage(HttpMethod.Options, "api/state");
            using HttpResponseMessage options = await client.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_request_for_another_host_name_is_refused()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync();
        using (server)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/state");
            request.Headers.Host = "evil.example";

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_disabled_server_does_not_listen()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(enabled: false);
        using (server)
        using (client)
        {
            Assert.Null(server.Address);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/state"));
        }
    }

    [Fact]
    public async Task A_busy_port_does_not_stop_the_application()
    {
        int port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            using var server = new ToolbarPanelServer(
                new FixedState(),
                Options.Create(new ToolbarPanelOptions { Port = port }),
                NullLogger<ToolbarPanelServer>.Instance,
                _folder);

            await server.StartAsync(CancellationToken.None);

            Assert.Null(server.Address);
        }
        finally
        {
            blocker.Stop();
        }
    }
}
