using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            new ToolbarPanelLabels("Dialogue ATC", "Phraséologie", "Vous", "Contrôleur", "-", "-", "-", "-", "-", "-"),
            [new ToolbarPanelTurn("1", "10:00", "pilot", "Vous", "Bonjour")],
            new ToolbarPanelPhraseology("Departure", "Point d'attente", [], []));
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
