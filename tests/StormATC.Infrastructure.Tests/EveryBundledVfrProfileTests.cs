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

        // The French column, when it was read, keeps the indices of the English notes.
        if (root.TryGetProperty("fr", out JsonElement french) && french.TryGetProperty("notes", out JsonElement frenchNotes))
        {
            Assert.Equal(root.GetProperty("notes").GetArrayLength(), frenchNotes.GetArrayLength());
            foreach (JsonElement note in frenchNotes.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, note.ValueKind);
            }
        }

        // The French of the procedures has the same tree as the English: nothing in "fr" may lack its English twin.
        if (french.ValueKind == JsonValueKind.Object)
        {
            foreach (string part in new[] { "circuit", "withAts", "withoutAts" })
            {
                if (french.TryGetProperty(part, out JsonElement frenchPart))
                {
                    Assert.True(root.TryGetProperty(part, out JsonElement englishPart), $"{icao}: fr.{part} has no English twin.");
                    AssertSameTree(icao + "." + part, englishPart, frenchPart);
                }
            }
        }

        if (french.ValueKind == JsonValueKind.Object && french.TryGetProperty("reportingPoints", out JsonElement frenchPoints)
            && root.TryGetProperty("reportingPoints", out JsonElement englishPoints))
        {
            var englishNames = englishPoints.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToHashSet();
            foreach (JsonElement point in frenchPoints.EnumerateArray())
            {
                Assert.Contains(point.GetProperty("name").GetString(), englishNames);
            }
        }

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

            // The base is read by French pilots: the name and the purpose of a point are French.
            foreach (string field in new[] { "location", "purpose" })
            {
                if (point.TryGetProperty(field, out JsonElement fieldText))
                {
                    Assert.DoesNotMatch(
                        @"(?i)(the|of|north|south|west|east|bridge|crossroads?|tower|reporting|runway|arrivals?|departures?)",
                        fieldText.GetString()!);
                }
            }

            // What the controller says: every letter in the international spelling alphabet and every digit, in both languages.
            if (point.TryGetProperty("spoken", out JsonElement spoken))
            {
                string letters = new string(name.Where(char.IsLetterOrDigit).ToArray());
                foreach (string language in new[] { "fr", "en" })
                {
                    string said = spoken.GetProperty(language).GetString()!;
                    Assert.Equal(letters.Length, said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                    for (int i = 0; i < letters.Length; i++)
                    {
                        string word = said.Split(' ', StringSplitOptions.RemoveEmptyEntries)[i];
                        Assert.True(
                            char.IsDigit(letters[i]) ? word.Length > 1 : word[0] == char.ToUpperInvariant(letters[i]) || (letters[i] is 'E' && word[0] is 'É') || (letters[i] is 'H' && word[0] is 'H') || (letters[i] is 'Q' && word[0] is 'Q') || (letters[i] is 'Z' && word[0] is 'Z') || (letters[i] is 'X' && word[0] is 'X'),
                            $"{icao} {name}: \"{said}\" ({language}) does not spell the letter {letters[i]}.");
                    }
                }
            }

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

    private static void AssertSameTree(string where, JsonElement english, JsonElement french)
    {
        if (french.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(JsonValueKind.Object, english.ValueKind);
            foreach (JsonProperty property in french.EnumerateObject())
            {
                Assert.True(english.TryGetProperty(property.Name, out JsonElement twin), $"{where}.{property.Name} is not in the English.");
                AssertSameTree(where + "." + property.Name, twin, property.Value);
            }
        }
        else if (french.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(JsonValueKind.Array, english.ValueKind);
            Assert.True(french.GetArrayLength() <= english.GetArrayLength(), $"{where}: longer than the English.");
            for (int i = 0; i < french.GetArrayLength(); i++)
            {
                AssertSameTree($"{where}[{i}]", english[i], french[i]);
            }
        }
        else
        {
            Assert.Equal(JsonValueKind.String, french.ValueKind);
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
