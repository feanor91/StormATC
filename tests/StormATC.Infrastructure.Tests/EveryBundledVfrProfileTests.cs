using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Infrastructure.Data;

namespace StormATC.Infrastructure.Tests;

/// <summary>What every VFR profile that ships with StormATC must satisfy, whoever or whatever wrote it.</summary>
public sealed class EveryBundledVfrProfileTests
{
    private const double MaximumPointDistanceNauticalMiles = 80;

    private static string BundledFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "vfr-profiles"));

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.EnumerateFiles(BundledFolder(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    private sealed class Paths(string root) : IStoragePaths
    {
        public string ApplicationDataDirectory => Path.Combine(root, "no-user-folder");

        public string DatabasePath => Path.Combine(root, "test.db");

        public string LogDirectory => root;

        public string FlightPlanCacheDirectory => root;

        public string PromptDirectory => root;

        public string DataDirectory => root;

        public void EnsureCreated()
        {
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task The_profile_loads_and_its_reporting_points_make_sense(string icao)
    {
        string path = Path.Combine(BundledFolder(), icao + ".json");
        string text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain('�', text);

        // The engine reads it.
        string dataRoot = Path.GetFullPath(Path.Combine(BundledFolder(), ".."));
        var provider = new JsonVfrProfileProvider(new Paths(dataRoot), NullLogger<JsonVfrProfileProvider>.Instance);
        Assert.NotNull(await provider.GetProfileAsync(icao, CancellationToken.None));

        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        Assert.Equal(icao, root.GetProperty("icao").GetString());

        if (!root.TryGetProperty("reportingPoints", out JsonElement points))
        {
            return;
        }

        JsonElement reference = root.GetProperty("referencePosition");
        double referenceLatitude = reference.GetProperty("latitude").GetDouble();
        double referenceLongitude = reference.GetProperty("longitude").GetDouble();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement point in points.EnumerateArray())
        {
            string name = point.GetProperty("name").GetString()!;
            Assert.True(names.Add(name), $"{icao}: the reporting point {name} is listed twice.");

            bool hasLatitude = point.TryGetProperty("latitude", out JsonElement latitude);
            bool hasLongitude = point.TryGetProperty("longitude", out JsonElement longitude);
            Assert.Equal(hasLatitude, hasLongitude);

            if (hasLatitude && hasLongitude)
            {
                double distance = DistanceNauticalMiles(
                    referenceLatitude, referenceLongitude, latitude.GetDouble(), longitude.GetDouble());
                Assert.True(
                    distance <= MaximumPointDistanceNauticalMiles,
                    $"{icao}: the reporting point {name} lies {distance:F0} NM from the aerodrome; check its coordinates.");
            }
        }
    }

    private static double DistanceNauticalMiles(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double EarthRadiusNauticalMiles = 3440.065;
        double phi1 = latitude1 * Math.PI / 180;
        double phi2 = latitude2 * Math.PI / 180;
        double deltaPhi = phi2 - phi1;
        double deltaLambda = (longitude2 - longitude1) * Math.PI / 180;
        double a = Math.Pow(Math.Sin(deltaPhi / 2), 2) + (Math.Cos(phi1) * Math.Cos(phi2) * Math.Pow(Math.Sin(deltaLambda / 2), 2));
        return 2 * EarthRadiusNauticalMiles * Math.Asin(Math.Sqrt(a));
    }
}
