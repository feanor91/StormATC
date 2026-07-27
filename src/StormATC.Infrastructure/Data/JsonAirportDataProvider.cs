using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Data;

/// <summary>
/// Loads aerodrome definitions from JSON files on disk.
/// </summary>
/// <remarks>
/// <para>
/// StormATC bundles only fictional sample aerodromes. This provider reads whatever JSON is in
/// the data directory, so a user can add their own without rebuilding — and so a future
/// licensed navigation-data provider can be dropped in behind the same interface.
/// </para>
/// <para>
/// Parsing is defensive throughout. A malformed aerodrome file yields null and a logged error,
/// never an exception that would take the session down; the engine then refuses to issue
/// clearances that depend on that aerodrome, which is the correct degraded behaviour.
/// </para>
/// </remarks>
public sealed class JsonAirportDataProvider(
    IStoragePaths storagePaths,
    ILogger<JsonAirportDataProvider> logger) : IAirportDataProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<JsonAirportDataProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<AirportData?> GetAirportAsync(string icao, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        string path = Path.Combine(
            _storagePaths.DataDirectory,
            "sample-airports",
            $"{icao.ToUpperInvariant()}.json");

        if (!File.Exists(path))
        {
            _logger.LogInformation("No airport data file found for {Icao} at {Path}.", icao, path);
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            using (document)
            {
                return ParseAirport(document.RootElement, icao);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            _logger.LogError(ex, "Airport data for {Icao} is invalid and was not loaded.", icao);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAvailableAirportsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string directory = Path.Combine(_storagePaths.DataDirectory, "sample-airports");
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var codes = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            codes.Add(Path.GetFileNameWithoutExtension(file).ToUpperInvariant());
        }

        codes.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(codes);
    }

    private AirportData ParseAirport(JsonElement root, string icao)
    {
        var runways = new List<RunwayData>();
        if (root.TryGetProperty("runways", out JsonElement runwaysElement)
            && runwaysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in runwaysElement.EnumerateArray())
            {
                RunwayData? runway = ParseRunway(element);
                if (runway is not null)
                {
                    runways.Add(runway);
                }
            }
        }

        var stands = new List<StandData>();
        if (root.TryGetProperty("stands", out JsonElement standsElement)
            && standsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in standsElement.EnumerateArray())
            {
                StandData? stand = ParseStand(element);
                if (stand is not null)
                {
                    stands.Add(stand);
                }
            }
        }

        return new AirportData
        {
            Icao = ReadString(root, "icao") ?? icao.ToUpperInvariant(),
            Name = ReadString(root, "name") ?? icao.ToUpperInvariant(),
            ReferencePosition = ReadPosition(root, "referencePosition") ?? GeoPosition.Create(0, 0),
            ElevationFeet = ReadInt(root, "elevationFeet") ?? 0,
            MagneticVariationDegrees = ReadDouble(root, "magneticVariationDegrees") ?? 0,
            IsFictional = ReadBool(root, "isFictional") ?? true,
            Runways = runways,
            Stands = stands,
            Frequencies = ParseFrequencies(root),
            TaxiGraph = ParseTaxiGraph(root),
        };
    }

    private RunwayData? ParseRunway(JsonElement element)
    {
        string? identifierText = ReadString(element, "identifier");
        if (identifierText is null || !RunwayIdentifier.TryCreate(identifierText, out RunwayIdentifier identifier))
        {
            _logger.LogWarning("Skipping runway with invalid identifier '{Identifier}'.", identifierText);
            return null;
        }

        GeoPosition? threshold = ReadPosition(element, "thresholdPosition");
        GeoPosition? end = ReadPosition(element, "endPosition");

        if (threshold is null || end is null)
        {
            _logger.LogWarning("Skipping runway {Identifier}: missing threshold or end position.", identifier);
            return null;
        }

        return new RunwayData
        {
            Identifier = identifier,
            ThresholdPosition = threshold.Value,
            EndPosition = end.Value,
            LengthFeet = ReadInt(element, "lengthFeet") ?? 0,
            WidthFeet = ReadInt(element, "widthFeet") ?? 0,
            Surface = ReadString(element, "surface"),
            IsPreferred = ReadBool(element, "isPreferred") ?? false,
            Approaches = ReadStringArray(element, "approaches"),
            HoldingPointNodes = ReadStringArray(element, "holdingPointNodes"),
            ExitNodes = ReadStringArray(element, "exitNodes"),
        };
    }

    private StandData? ParseStand(JsonElement element)
    {
        string? name = ReadString(element, "name");
        GeoPosition? position = ReadPosition(element, "position");
        string? node = ReadString(element, "taxiNodeName");

        if (name is null || position is null || node is null)
        {
            _logger.LogWarning("Skipping stand with missing name, position, or taxi node.");
            return null;
        }

        return new StandData
        {
            Name = name,
            Position = position.Value,
            TaxiNodeName = node,
            SuitableFor = ReadStringArray(element, "suitableFor"),
            RequiresPushback = ReadBool(element, "requiresPushback") ?? true,
        };
    }

    private static AirportFrequencies ParseFrequencies(JsonElement root)
    {
        if (!root.TryGetProperty("frequencies", out JsonElement element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return new AirportFrequencies();
        }

        return new AirportFrequencies
        {
            ClearanceDelivery = ReadFrequency(element, "clearanceDelivery"),
            Ground = ReadFrequency(element, "ground"),
            Tower = ReadFrequency(element, "tower"),
            Departure = ReadFrequency(element, "departure"),
            Approach = ReadFrequency(element, "approach"),
            Center = ReadFrequency(element, "center"),
            Atis = ReadFrequency(element, "atis"),
        };
    }

    private TaxiGraph? ParseTaxiGraph(JsonElement root)
    {
        if (!root.TryGetProperty("taxiGraph", out JsonElement element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var nodes = new List<TaxiNode>();
        if (element.TryGetProperty("nodes", out JsonElement nodesElement)
            && nodesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement nodeElement in nodesElement.EnumerateArray())
            {
                string? name = ReadString(nodeElement, "name");
                GeoPosition? position = ReadPosition(nodeElement, "position");

                if (name is null || position is null)
                {
                    continue;
                }

                RunwayIdentifier? associatedRunway = null;
                string? runwayText = ReadString(nodeElement, "associatedRunway");
                if (runwayText is not null && RunwayIdentifier.TryCreate(runwayText, out RunwayIdentifier parsed))
                {
                    associatedRunway = parsed;
                }

                nodes.Add(new TaxiNode
                {
                    Name = name,
                    Position = position.Value,
                    Kind = ParseNodeKind(ReadString(nodeElement, "kind")),
                    AssociatedRunway = associatedRunway,
                    StandName = ReadString(nodeElement, "standName"),
                });
            }
        }

        var edges = new List<TaxiEdge>();
        if (element.TryGetProperty("edges", out JsonElement edgesElement)
            && edgesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edgeElement in edgesElement.EnumerateArray())
            {
                string? from = ReadString(edgeElement, "from");
                string? to = ReadString(edgeElement, "to");
                string? taxiway = ReadString(edgeElement, "taxiway");

                if (from is null || to is null || taxiway is null)
                {
                    continue;
                }

                RunwayIdentifier? crossed = null;
                string? crossedText = ReadString(edgeElement, "crossedRunway");
                if (crossedText is not null && RunwayIdentifier.TryCreate(crossedText, out RunwayIdentifier parsed))
                {
                    crossed = parsed;
                }

                edges.Add(new TaxiEdge
                {
                    FromNode = from,
                    ToNode = to,
                    TaxiwayName = taxiway,
                    LengthMeters = ReadDouble(edgeElement, "lengthMeters") ?? 100,
                    IsOneWay = ReadBool(edgeElement, "isOneWay") ?? false,
                    CrossesRunway = ReadBool(edgeElement, "crossesRunway") ?? false,
                    CrossedRunway = crossed,
                });
            }
        }

        if (nodes.Count == 0)
        {
            _logger.LogWarning("Taxi graph has no valid nodes; taxi routing will be unavailable.");
            return null;
        }

        return TaxiGraph.Build(nodes, edges);
    }

    private static TaxiNodeKind ParseNodeKind(string? kind) => kind switch
    {
        "Stand" => TaxiNodeKind.Stand,
        "HoldingPoint" => TaxiNodeKind.HoldingPoint,
        "RunwayEntry" => TaxiNodeKind.RunwayEntry,
        "RunwayExit" => TaxiNodeKind.RunwayExit,
        _ => TaxiNodeKind.Intersection,
    };

    // ---------------------------------------------------------------------------------
    // Defensive readers. Every one returns null on any problem rather than throwing, so a
    // partially malformed file still yields as much usable data as it contains.
    // ---------------------------------------------------------------------------------

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double result)
            ? result
            : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static GeoPosition? ReadPosition(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? latitude = ReadDouble(value, "latitude");
        double? longitude = ReadDouble(value, "longitude");

        return latitude is double lat
            && longitude is double lon
            && GeoPosition.TryCreate(lat, lon, out GeoPosition position)
                ? position
                : null;
    }

    private static Frequency? ReadFrequency(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDecimal(out decimal megahertz)
            && Frequency.TryFromMegahertz(megahertz, out Frequency frequency)
                ? frequency
                : null;
    }

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                results.Add(text);
            }
        }

        return results;
    }
}
