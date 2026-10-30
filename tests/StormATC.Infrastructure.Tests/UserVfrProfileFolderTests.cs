using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Infrastructure.Data;

namespace StormATC.Infrastructure.Tests;

/// <summary>The VFR profiles in the user's own folder win over the ones that come with StormATC.</summary>
public sealed class UserVfrProfileFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stormatc-userprofiles-" + Guid.NewGuid().ToString("N"));

    public UserVfrProfileFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "vfr-profiles"));
        Directory.CreateDirectory(Path.Combine(_root, "appdata", "vfr-profiles"));
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

    private JsonVfrProfileProvider Provider() => new(new Paths(_root), NullLogger<JsonVfrProfileProvider>.Instance);

    private static string Profile(string icao, int qfu) =>
        $$"""{ "icao": "{{icao}}", "name": "Test", "preferredQfu": {{qfu}} }""";

    [Fact]
    public async Task A_profile_in_the_user_folder_is_read_before_the_bundled_one()
    {
        File.WriteAllText(Path.Combine(_root, "data", "vfr-profiles", "LFPM.json"), Profile("LFPM", 100));
        File.WriteAllText(Path.Combine(_root, "appdata", "vfr-profiles", "LFPM.json"), Profile("LFPM", 282));

        VfrAerodromeProfile? profile = await Provider().GetProfileAsync("LFPM", CancellationToken.None);

        Assert.Equal(282, profile!.PreferredQfu);
    }

    [Fact]
    public async Task Without_a_user_profile_the_bundled_one_is_read()
    {
        File.WriteAllText(Path.Combine(_root, "data", "vfr-profiles", "LFPN.json"), Profile("LFPN", 69));

        VfrAerodromeProfile? profile = await Provider().GetProfileAsync("LFPN", CancellationToken.None);

        Assert.Equal(69, profile!.PreferredQfu);
    }

    [Fact]
    public async Task A_profile_only_in_the_user_folder_is_read()
    {
        File.WriteAllText(Path.Combine(_root, "appdata", "vfr-profiles", "LFOZ.json"), Profile("LFOZ", 232));

        VfrAerodromeProfile? profile = await Provider().GetProfileAsync("LFOZ", CancellationToken.None);

        Assert.Equal(232, profile!.PreferredQfu);
        Assert.Null(await Provider().GetProfileAsync("LFXX", CancellationToken.None));
    }
}

/// <summary>The directory of every VFR profile, used to find an aerodrome from a frequency.</summary>
public sealed class VfrProfileDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stormatc-directory-" + Guid.NewGuid().ToString("N"));

    public VfrProfileDirectoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "vfr-profiles"));
        Directory.CreateDirectory(Path.Combine(_root, "appdata", "vfr-profiles"));
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

    [Fact]
    public async Task Every_profile_is_listed_and_the_users_own_replaces_the_bundled_one()
    {
        File.WriteAllText(
            Path.Combine(_root, "data", "vfr-profiles", "LFAT.json"),
            """{ "icao": "LFAT", "name": "Bundled", "frequencies": { "atis": 123.13 } }""");
        File.WriteAllText(
            Path.Combine(_root, "data", "vfr-profiles", "LFAB.json"),
            """{ "icao": "LFAB", "name": "Dieppe", "frequencies": { "airAir": 119.005 } }""");
        File.WriteAllText(
            Path.Combine(_root, "appdata", "vfr-profiles", "LFAT.json"),
            """{ "icao": "LFAT", "name": "Mine", "frequencies": { "atis": 124.5 } }""");

        var provider = new JsonVfrProfileProvider(new Paths(_root), NullLogger<JsonVfrProfileProvider>.Instance);
        IReadOnlyList<VfrAerodromeProfile> all = await provider.ListProfilesAsync(CancellationToken.None);

        Assert.Equal(["LFAB", "LFAT"], all.Select(p => p.Icao).Order());
        Assert.Equal("Mine", all.Single(p => p.Icao == "LFAT").Name);
        Assert.Equal(
            "ATIS 124.500",
            StormATC.Application.Weather.AerodromeFrequencyLookup.Summarize(all.Single(p => p.Icao == "LFAT")));
    }
}
