using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Charts;
using StormATC.Application.Options;
using StormATC.Application.Panel;
using StormATC.Infrastructure.Charts;
using StormATC.Infrastructure.Panel;

namespace StormATC.Infrastructure.Tests;

/// <summary>The charts the pilot puts in the charts folder, and how the panel asks for them.</summary>
public sealed class VacChartLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-vac-" + Guid.NewGuid().ToString("N"));

    public VacChartLibraryTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeRenderer(int pages = 3) : IVacPageRenderer
    {
        public int Draws { get; private set; }

        public Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken) => Task.FromResult(pages);

        public Task<byte[]> RenderPageAsync(string pdfPath, int page, int width, CancellationToken cancellationToken)
        {
            Draws++;
            return Task.FromResult(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', (byte)page, (byte)(width % 251) });
        }
    }

    private void Chart(string name) => File.WriteAllText(Path.Combine(_folder, name), "%PDF-1.4");

    private VacChartLibrary Library(IVacPageRenderer? renderer = null) =>
        new(_folder, NullLogger<VacChartLibrary>.Instance, renderer);

    [Theory]
    [InlineData("AD-2.LFPL.pdf", "AD-2.LFPL", "LFPL")]
    [InlineData("AD-2.LFPL (1).pdf", "AD-2.LFPL", "LFPL")]
    [InlineData("VAC LFPN toussus.pdf", "VAC LFPN toussus", "LFPN")]
    [InlineData("carte generale.pdf", "carte generale", null)]
    [InlineData("EGLL approach.pdf", "EGLL approach", "EGLL")]
    public void A_file_name_gives_the_chart_name_and_its_aerodrome(string file, string name, string? icao) =>
        Assert.Equal((name, icao), VacChartNames.Describe(file));

    [Fact]
    public void The_charts_are_listed_by_name_and_a_copy_is_listed_once()
    {
        Chart("AD-2.LFPO.pdf");
        Chart("AD-2.LFFP.pdf");
        Chart("AD-2.LFFP (1).pdf");
        Chart("AD-2.LFFP (2).pdf");
        Chart("notes.txt");
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllText(Path.Combine(_folder, "sub", "hidden.pdf"), "%PDF");

        IReadOnlyList<VacChart> charts = Library().List();

        Assert.Equal(["AD-2.LFFP", "AD-2.LFPO"], charts.Select(c => c.Id));
        Assert.Equal(["LFFP", "LFPO"], charts.Select(c => c.Icao));
    }

    [Fact]
    public void A_missing_folder_lists_nothing_and_is_created_on_request()
    {
        string missing = Path.Combine(_folder, "later");
        var library = new VacChartLibrary(missing, NullLogger<VacChartLibrary>.Instance, null);

        Assert.Empty(library.List());
        library.EnsureDirectory();

        Assert.True(Directory.Exists(missing));
    }

    [Fact]
    public async Task A_page_is_drawn_once_and_kept()
    {
        Chart("AD-2.LFPL.pdf");
        var renderer = new FakeRenderer();
        VacChartLibrary library = Library(renderer);

        VacChartPage? first = await library.RenderPageAsync("AD-2.LFPL", 2, 1400, CancellationToken.None);
        VacChartPage? again = await library.RenderPageAsync("ad-2.lfpl", 2, 1400, CancellationToken.None);
        VacChartPage? wider = await library.RenderPageAsync("AD-2.LFPL", 2, 2000, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(2, first.PageNumber);
        Assert.Equal(3, first.PageCount);
        Assert.Same(first.Png, again!.Png);
        Assert.NotNull(wider);
        Assert.Equal(2, renderer.Draws);
    }

    [Theory]
    [InlineData("AD-2.LFPL", 0)]
    [InlineData("AD-2.LFPL", 4)]
    [InlineData("AD-2.LFXX", 1)]
    [InlineData("", 1)]
    [InlineData("..\\AD-2.LFPL", 1)]
    [InlineData("../AD-2.LFPL", 1)]
    [InlineData("C:\\Windows\\win", 1)]
    public async Task A_page_or_a_chart_that_does_not_exist_is_not_drawn(string id, int page)
    {
        Chart("AD-2.LFPL.pdf");

        Assert.Null(await Library(new FakeRenderer()).RenderPageAsync(id, page, 1400, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_renderer_no_page_is_drawn()
    {
        Chart("AD-2.LFPL.pdf");

        Assert.Null(await Library().GetPageCountAsync("AD-2.LFPL", CancellationToken.None));
        Assert.Null(await Library().RenderPageAsync("AD-2.LFPL", 1, 1400, CancellationToken.None));
    }

    [Fact]
    public void The_folder_is_the_one_in_the_settings_or_the_one_beside_the_application()
    {
        string beside = Path.Combine(_folder, "app");
        Directory.CreateDirectory(beside);

        Assert.Equal(Path.Combine(_folder, "mine"), VacChartLibrary.Resolve(Path.Combine(_folder, "mine"), beside));
        Assert.Equal(Path.Combine(beside, "Cartes VAC"), VacChartLibrary.Resolve(null, beside));
    }

    [Fact]
    public void A_developers_checkout_uses_the_charts_at_the_root_of_the_repository()
    {
        string root = Path.Combine(_folder, "repo");
        string build = Path.Combine(root, "src", "Desktop", "bin", "Release");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(root, "StormATC.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "Cartes VAC"));
        File.WriteAllText(Path.Combine(root, "Cartes VAC", "AD-2.LFPL.pdf"), "%PDF");

        Assert.Equal(Path.Combine(root, "Cartes VAC"), VacChartLibrary.Resolve(null, build));
    }

    // ---- the panel's requests --------------------------------------------------------------------

    private sealed class FixedState : IToolbarPanelStateSource
    {
        public ToolbarPanelState Current() => new(
            "fr", "F-GABC", "Tour", "118.600", "VFR",
            new ToolbarPanelPhase("Departure", "Point d'attente"),
            new ToolbarPanelLabels("Dialogue ATC", "Phraséologie", "Vous", "Contrôleur", "-", "-", "-", "-", "-", "-", "-", "-"),
            [],
            new ToolbarPanelPhraseology("Departure", "Point d'attente", [], [], []));
    }

    private async Task<(ToolbarPanelServer Server, HttpClient Client)> StartAsync(IVacChartLibrary? charts)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var server = new ToolbarPanelServer(
            new FixedState(),
            Options.Create(new ToolbarPanelOptions { Port = port }),
            NullLogger<ToolbarPanelServer>.Instance,
            _folder,
            charts: charts);
        await server.StartAsync(CancellationToken.None);
        return (server, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task The_panel_lists_the_charts_and_asks_for_their_pages()
    {
        Chart("AD-2.LFPL.pdf");
        Chart("AD-2.LFPL (1).pdf");
        VacChartLibrary library = Library(new FakeRenderer());
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(library);
        using (server)
        using (client)
        {
            using JsonDocument list = JsonDocument.Parse(await client.GetStringAsync("api/charts"));
            Assert.Equal(_folder, list.RootElement.GetProperty("directory").GetString());
            JsonElement charts = list.RootElement.GetProperty("charts");
            Assert.Equal(1, charts.GetArrayLength());
            Assert.Equal("LFPL", charts[0].GetProperty("icao").GetString());

            using JsonDocument info = JsonDocument.Parse(await client.GetStringAsync("api/charts/AD-2.LFPL"));
            Assert.Equal(3, info.RootElement.GetProperty("pages").GetInt32());

            using HttpResponseMessage page = await client.GetAsync("api/charts/AD-2.LFPL/2.png?w=1200");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("image/png", page.Content.Headers.ContentType?.MediaType);
            Assert.Equal(0x89, (await page.Content.ReadAsByteArrayAsync())[0]);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("api/charts/unknown")]
    [InlineData("api/charts/AD-2.LFPL/0.png")]
    [InlineData("api/charts/AD-2.LFPL/9.png")]
    [InlineData("api/charts/AD-2.LFPL/1.jpg")]
    [InlineData("api/charts/AD-2.LFPL/1/2.png")]
    [InlineData("api/charts/..%2F..%2Fsecret/1.png")]
    [InlineData("api/charts/C%3A%5CWindows%5Cwin/1.png")]
    public async Task A_request_that_does_not_name_a_listed_page_is_refused(string path)
    {
        Chart("AD-2.LFPL.pdf");
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(Library(new FakeRenderer()));
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_a_chart_library_the_panel_has_no_charts()
    {
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(null);
        using (server)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("api/charts");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_server_makes_the_charts_folder()
    {
        string later = Path.Combine(_folder, "made");
        var library = new VacChartLibrary(later, NullLogger<VacChartLibrary>.Instance, null);
        (ToolbarPanelServer server, HttpClient client) = await StartAsync(library);
        using (server)
        using (client)
        {
            Assert.True(Directory.Exists(later));
            await server.StopAsync(CancellationToken.None);
        }
    }
}
