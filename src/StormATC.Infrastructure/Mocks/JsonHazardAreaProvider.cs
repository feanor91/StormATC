using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Mocks;

/// <summary>
/// Supplies the hazard areas from the optional file <c>hazard-areas.json</c> in the application data folder, which the user
/// keeps up to date (no bundled data: a permanent area list would go stale, and a wrong area is worse than none).
/// The file is read again when it changes.
/// </summary>
/// <remarks>
/// The format is <c>{ "areas": [ { "name": "R 45", "kind": "military", "latitude": 48.1, "longitude": 2.3,
/// "radiusNm": 10, "floorFeet": 0, "ceilingFeet": 19500, "active": true } ] }</c>; <c>kind</c> is
/// <c>military</c> or <c>weather</c>.
/// </remarks>
public sealed class JsonHazardAreaProvider(
    IStoragePaths storagePaths,
    ILogger<JsonHazardAreaProvider> logger) : IHazardAreaProvider
{
    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<JsonHazardAreaProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private IReadOnlyList<HazardArea> _cached = [];

    /// <inheritdoc />
    public async Task<IReadOnlyList<HazardArea>> GetActiveAreasAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_storagePaths.ApplicationDataDirectory, "hazard-areas.json");
        if (!File.Exists(path))
        {
            _cached = [];
            _loadedWriteTimeUtc = DateTime.MinValue;
            return _cached;
        }

        DateTime writeTime = File.GetLastWriteTimeUtc(path);
        if (writeTime == _loadedWriteTimeUtc)
        {
            return _cached;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var areas = new List<HazardArea>();

            if (document.RootElement.TryGetProperty("areas", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in array.EnumerateArray())
                {
                    if (Parse(element) is HazardArea area && area.IsActive)
                    {
                        areas.Add(area);
                    }
                }
            }

            _cached = areas;
            _loadedWriteTimeUtc = writeTime;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "The hazard area file {Path} could not be read; no area is used.", path);
            _cached = [];
            _loadedWriteTimeUtc = writeTime;
        }

        return _cached;
    }

    private static HazardArea? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("name", out JsonElement name) || name.GetString() is not { Length: > 0 } text
            || !element.TryGetProperty("latitude", out JsonElement latitude) || !latitude.TryGetDouble(out double lat)
            || !element.TryGetProperty("longitude", out JsonElement longitude) || !longitude.TryGetDouble(out double lon)
            || !element.TryGetProperty("radiusNm", out JsonElement radius) || !radius.TryGetDouble(out double miles)
            || miles <= 0
            || !GeoPosition.TryCreate(lat, lon, out GeoPosition center))
        {
            return null;
        }

        bool weather = element.TryGetProperty("kind", out JsonElement kind)
            && string.Equals(kind.GetString(), "weather", StringComparison.OrdinalIgnoreCase);

        return new HazardArea
        {
            Name = text,
            Kind = weather ? HazardKind.Weather : HazardKind.MilitaryArea,
            Center = center,
            RadiusNauticalMiles = miles,
            FloorFeet = element.TryGetProperty("floorFeet", out JsonElement floor) && floor.TryGetDouble(out double low) ? low : 0,
            CeilingFeet = element.TryGetProperty("ceilingFeet", out JsonElement ceiling) && ceiling.TryGetDouble(out double high) ? high : 99_999,
            IsActive = !element.TryGetProperty("active", out JsonElement active) || active.ValueKind != JsonValueKind.False,
        };
    }
}
