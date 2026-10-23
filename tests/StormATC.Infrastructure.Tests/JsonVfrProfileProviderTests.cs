using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Domain.Intents;
using StormATC.Domain.ValueObjects;
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

    private static string BundledFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "vfr-profiles"));

    [Theory]
    [InlineData("LFFB", 277, null, 0)]
    [InlineData("LFFP", null, null, 0)]
    [InlineData("LFHP", 329, null, 0)]
    [InlineData("LFOX", 56, null, 0)]
    [InlineData("LFOZ", 232, null, 0)]
    [InlineData("LFOR", 91, null, 0)]
    [InlineData("LFPL", 80, 1200, 1)]
    [InlineData("LFPM", 282, null, 3)]
    [InlineData("LFPN", null, 1500, 2)]
    [InlineData("LFPO", null, null, 0)]
    [InlineData("LFSI", 110, null, 0)]
    public async Task Every_bundled_profile_loads_with_what_the_chart_says(
        string icao, int? preferredQfu, int? circuitHeight, int reportingPoints)
    {
        string bundled = Path.Combine(BundledFolder(), icao + ".json");
        Assert.True(File.Exists(bundled), $"Expected the bundled profile at {bundled}");
        File.Copy(bundled, Path.Combine(_data, "vfr-profiles", icao + ".json"));

        VfrAerodromeProfile? profile = await Provider().GetProfileAsync(icao, CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal(icao, profile.Icao);
        Assert.False(string.IsNullOrWhiteSpace(profile.Name));
        Assert.Equal(preferredQfu, profile.PreferredQfu);
        Assert.Equal(circuitHeight, profile.CircuitHeightFeetAmsl);
        Assert.Equal(reportingPoints, profile.ReportingPoints.Count);
    }

    [Theory]
    [InlineData("LFFB", true, 123.155)]
    [InlineData("LFFP", true, 123.500)]
    [InlineData("LFOR", true, 119.205)]
    [InlineData("LFOX", true, 119.050)]
    [InlineData("LFHP", false, 118.005)]
    [InlineData("LFOZ", false, 122.405)]
    [InlineData("LFPL", false, 118.605)]
    [InlineData("LFPM", false, 121.105)]
    [InlineData("LFPN", false, 120.755)]
    [InlineData("LFPO", false, null)]
    public async Task The_profiles_say_which_aerodromes_have_no_air_traffic_service(string icao, bool noAtc, double? airAir)
    {
        File.Copy(Path.Combine(BundledFolder(), icao + ".json"), Path.Combine(_data, "vfr-profiles", icao + ".json"));

        VfrAerodromeProfile profile = (await Provider().GetProfileAsync(icao, CancellationToken.None))!;

        Assert.Equal(noAtc, profile.HasNoAtc);
        Assert.Equal(
            airAir is double expected ? Frequency.FromMegahertz((decimal)expected) : null,
            profile.AirAirFrequency);
    }

    [Fact]
    public async Task Every_bundled_profile_loads_and_is_named_after_its_file()
    {
        string[] files = Directory.GetFiles(BundledFolder(), "*.json");
        Assert.True(files.Length > 400, $"Expected the bundled profiles, found {files.Length}");

        foreach (string file in files)
        {
            string icao = Path.GetFileNameWithoutExtension(file);
            File.Copy(file, Path.Combine(_data, "vfr-profiles", icao + ".json"), overwrite: true);

            VfrAerodromeProfile? profile = await Provider().GetProfileAsync(icao, CancellationToken.None);

            Assert.True(profile is not null, $"{icao} does not load");
            Assert.Equal(icao, profile.Icao);
            Assert.False(string.IsNullOrWhiteSpace(profile.Name), $"{icao} has no name");
        }
    }

    [Theory]
    [InlineData("LFAB", true, 119.005)]
    [InlineData("LFBO", false, null)]
    [InlineData("LFBC", false, null)]
    public async Task The_extracted_profiles_tell_controlled_from_uncontrolled_aerodromes(string icao, bool noAtc, double? airAir)
    {
        File.Copy(Path.Combine(BundledFolder(), icao + ".json"), Path.Combine(_data, "vfr-profiles", icao + ".json"));

        VfrAerodromeProfile profile = (await Provider().GetProfileAsync(icao, CancellationToken.None))!;

        Assert.Equal(noAtc, profile.HasNoAtc);
        Assert.Equal(
            airAir is double expected ? Frequency.FromMegahertz((decimal)expected) : null,
            profile.AirAirFrequency);
    }

    [Fact]
    public async Task The_Chartres_circuit_lies_north_of_the_runways()
    {
        File.Copy(Path.Combine(BundledFolder(), "LFOR.json"), Path.Combine(_data, "vfr-profiles", "LFOR.json"));

        VfrAerodromeProfile profile = (await Provider().GetProfileAsync("LFOR", CancellationToken.None))!;

        // North of the runways: left-hand for 09 (landing east), right-hand for 27.
        Assert.Equal(CircuitSide.Left, profile.CircuitSideFor(91));
        Assert.Equal(CircuitSide.Right, profile.CircuitSideFor(271));
    }

    [Fact]
    public async Task The_reporting_points_of_Melun_and_Toussus_are_found_by_their_spoken_names()
    {
        foreach (string icao in new[] { "LFPM", "LFPN" })
        {
            File.Copy(Path.Combine(BundledFolder(), icao + ".json"), Path.Combine(_data, "vfr-profiles", icao + ".json"));
        }

        VfrAerodromeProfile melun = (await Provider().GetProfileAsync("LFPM", CancellationToken.None))!;
        VfrAerodromeProfile toussus = (await Provider().GetProfileAsync("LFPN", CancellationToken.None))!;

        Assert.Equal("N", melun.FindPoint("Novembre")?.Name);
        Assert.Equal("SM", melun.FindPoint("sierra mike")?.Name);
        Assert.Equal("B", melun.FindPoint("Bravo")?.Name);
        Assert.Equal("S", toussus.FindPoint("Sierra")?.Name);
        Assert.Equal("NH", toussus.FindPoint("November Hotel")?.Name);
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
