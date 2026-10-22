using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Charts;
using StormATC.Domain.Airports;
using StormATC.Infrastructure.Charts;
using StormATC.Infrastructure.Data;

namespace StormATC.Infrastructure.Tests;

/// <summary>The profiles the pilot confirmed after a model read a chart.</summary>
public sealed class UserVfrProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stormatc-userprofile-" + Guid.NewGuid().ToString("N"));

    public UserVfrProfileStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "vfr-profiles"));
        Directory.CreateDirectory(Path.Combine(_root, "appdata"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Paths(string root) : IStoragePaths
    {
        public string ApplicationDataDirectory => Path.Combine(root, "appdata");

        public string DatabasePath => Path.Combine(root, "test.db");

        public string LogDirectory => root;

        public string FlightPlanCacheDirectory => root;

        public string PromptDirectory => root;

        public string DataDirectory => Path.Combine(root, "data");

        public void EnsureCreated()
        {
        }
    }

    private UserVfrProfileStore Store() => new(new Paths(_root), NullLogger<UserVfrProfileStore>.Instance);

    private JsonVfrProfileProvider Provider() => new(new Paths(_root), NullLogger<JsonVfrProfileProvider>.Instance);

    private static string Profile(string icao, int qfu) =>
        $$"""{ "icao": "{{icao}}", "name": "Test", "preferredQfu": {{qfu}}, "reportingPoints": [ { "name": "N", "spoken": { "fr": "Novembre", "en": "November" } } ] }""";

    [Fact]
    public async Task A_saved_profile_is_read_before_the_bundled_one()
    {
        File.WriteAllText(Path.Combine(_root, "data", "vfr-profiles", "LFPM.json"), Profile("LFPM", 100));
        UserVfrProfileStore store = Store();
        Assert.Equal(VacProfileOrigin.Bundled, store.Origin("lfpm"));

        await store.SaveAsync("LFPM", Profile("LFPM", 282), CancellationToken.None);

        Assert.Equal(VacProfileOrigin.User, store.Origin("LFPM"));
        VfrAerodromeProfile? profile = await Provider().GetProfileAsync("LFPM", CancellationToken.None);
        Assert.Equal(282, profile!.PreferredQfu);
        Assert.Equal("N", Assert.Single(profile.ReportingPoints).Name);
    }

    [Fact]
    public async Task A_second_save_keeps_the_first_beside_it()
    {
        UserVfrProfileStore store = Store();

        string path = await store.SaveAsync("LFPN", Profile("LFPN", 69), CancellationToken.None);
        await store.SaveAsync("LFPN", Profile("LFPN", 249), CancellationToken.None);

        Assert.Contains("\"preferredQfu\": 249", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("\"preferredQfu\": 69", File.ReadAllText(path + ".bak"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_profile_of_either_kind_the_origin_is_none()
    {
        Assert.Equal(VacProfileOrigin.None, Store().Origin("LFXX"));
        Assert.Null(await Provider().GetProfileAsync("LFXX", CancellationToken.None));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"name\": \"no icao\" }")]
    public async Task What_is_not_a_readable_profile_is_not_written(string json)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Store().SaveAsync("LFPM", json, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "appdata", "vfr-profiles", "LFPM.json")));
    }

    [Fact]
    public async Task A_profile_for_another_aerodrome_is_not_written()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store().SaveAsync("LFPM", Profile("LFPN", 69), CancellationToken.None));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\LFPM")]
    [InlineData("LFP")]
    [InlineData("LF1M")]
    public async Task A_name_that_is_not_an_icao_code_is_refused(string icao)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().SaveAsync(icao, Profile("LFPM", 1), CancellationToken.None));
        Assert.Equal(VacProfileOrigin.None, Store().Origin(icao));
    }
}
