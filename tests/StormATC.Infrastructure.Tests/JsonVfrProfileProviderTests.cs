using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Infrastructure.Data;

namespace StormATC.Infrastructure.Tests;

/// <summary>Loading the VFR profile of an aerodrome.</summary>
public sealed class JsonVfrProfileProviderTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "stormatc-vfr-" + Guid.NewGuid().ToString("N"));

    public JsonVfrProfileProviderTests() => Directory.CreateDirectory(Path.Combine(_data, "vfr-profiles"));

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private sealed class Paths(string data) : IStoragePaths
    {
        public string ApplicationDataDirectory => data;

        public string DatabasePath => Path.Combine(data, "test.db");

        public string LogDirectory => data;

        public string FlightPlanCacheDirectory => data;

        public string PromptDirectory => data;

        public string DataDirectory => data;

        public void EnsureCreated()
        {
        }
    }

    private JsonVfrProfileProvider Provider() => new(new Paths(_data), NullLogger<JsonVfrProfileProvider>.Instance);

    [Fact]
    public async Task The_bundled_Lognes_profile_loads_with_its_reporting_point_and_circuit()
    {
        string bundled = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "vfr-profiles", "LFPL.json"));
        Assert.True(File.Exists(bundled), $"Expected the bundled profile at {bundled}");
        File.Copy(bundled, Path.Combine(_data, "vfr-profiles", "LFPL.json"));

        VfrAerodromeProfile? profile = await Provider().GetProfileAsync("lfpl", CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("LFPL", profile.Icao);
        VfrReportingPoint point = Assert.Single(profile.ReportingPoints);
        Assert.Equal("E", point.Name);
        Assert.Equal("Écho", point.SpokenFrench);
        Assert.Equal("Echo", point.SpokenEnglish);
        Assert.Equal(1200, profile.CircuitHeightFeetAmsl);
        Assert.Equal(80, profile.PreferredQfu);
        Assert.Same(point, profile.FindPoint("écho"));
        Assert.Same(point, profile.FindPoint("e"));
        Assert.Null(profile.FindPoint("Foxtrot"));

        // The circuit lies north of the runways: left-hand for 08, right-hand for 26.
        Assert.Equal(CircuitSide.Left, profile.CircuitSideFor(80));
        Assert.Equal(CircuitSide.Right, profile.CircuitSideFor(260));
    }

    [Theory]
    [InlineData(0, 80, CircuitSide.Left)]
    [InlineData(0, 260, CircuitSide.Right)]
    [InlineData(180, 80, CircuitSide.Right)]
    [InlineData(180, 260, CircuitSide.Left)]
    [InlineData(90, 360, CircuitSide.Right)]
    [InlineData(270, 360, CircuitSide.Left)]
    public void The_hand_follows_the_runway_in_use(int sideBearing, int runwayHeading, CircuitSide expected)
    {
        var profile = new VfrAerodromeProfile { Icao = "LFXX", CircuitSideBearing = sideBearing };

        Assert.Equal(expected, profile.CircuitSideFor(runwayHeading));
    }

    [Fact]
    public void Without_a_published_side_the_hand_is_unknown()
    {
        Assert.Null(new VfrAerodromeProfile { Icao = "LFXX" }.CircuitSideFor(80));
    }

    [Fact]
    public async Task A_missing_malformed_or_hostile_request_gives_null_not_an_exception()
    {
        File.WriteAllText(Path.Combine(_data, "vfr-profiles", "LFXX.json"), "{ not json");
        JsonVfrProfileProvider provider = Provider();

        Assert.Null(await provider.GetProfileAsync("LFAA", CancellationToken.None));
        Assert.Null(await provider.GetProfileAsync("LFXX", CancellationToken.None));
        Assert.Null(await provider.GetProfileAsync("..\\..\\secrets", CancellationToken.None));
        Assert.Null(await provider.GetProfileAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public void A_profile_without_an_icao_is_rejected()
    {
        using JsonDocument document = JsonDocument.Parse("{\"name\":\"Nowhere\"}");

        Assert.Null(JsonVfrProfileProvider.Parse(document.RootElement));
    }
}
