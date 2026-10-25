using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Infrastructure.Mocks;

namespace StormATC.Infrastructure.Tests;

/// <summary>Loading the optional file of hazard areas.</summary>
public sealed class JsonHazardAreaProviderTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "stormatc-hazard-" + Guid.NewGuid().ToString("N"));

    public JsonHazardAreaProviderTests() => Directory.CreateDirectory(_data);

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

    private JsonHazardAreaProvider Provider() => new(new Paths(_data), NullLogger<JsonHazardAreaProvider>.Instance);

    [Fact]
    public async Task Without_a_file_no_area_is_known()
    {
        Assert.Empty(await Provider().GetActiveAreasAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_active_areas_of_the_file_are_loaded_with_their_kind_and_levels()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_data, "hazard-areas.json"),
            """
            { "areas": [
              { "name": "R 45", "kind": "military", "latitude": 48.1, "longitude": 2.3, "radiusNm": 10, "floorFeet": 0, "ceilingFeet": 19500 },
              { "name": "CB", "kind": "weather", "latitude": 47.0, "longitude": 3.0, "radiusNm": 15 },
              { "name": "R 46", "kind": "military", "latitude": 46.0, "longitude": 3.0, "radiusNm": 8, "active": false },
              { "name": "Broken", "latitude": 99, "longitude": 3.0, "radiusNm": 8 }
            ] }
            """);

        IReadOnlyList<HazardArea> areas = await Provider().GetActiveAreasAsync(CancellationToken.None);

        Assert.Equal(["R 45", "CB"], areas.Select(area => area.Name));
        Assert.Equal(HazardKind.MilitaryArea, areas[0].Kind);
        Assert.Equal(19_500, areas[0].CeilingFeet);
        Assert.Equal(HazardKind.Weather, areas[1].Kind);
        Assert.Equal(99_999, areas[1].CeilingFeet);
    }

    [Fact]
    public async Task A_file_that_is_not_json_gives_no_area()
    {
        await File.WriteAllTextAsync(Path.Combine(_data, "hazard-areas.json"), "not json");

        Assert.Empty(await Provider().GetActiveAreasAsync(CancellationToken.None));
    }
}
