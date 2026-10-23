using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Mocks;

/// <summary>
/// Supplies the bundled sample flight plan, so the full pipeline runs with no SimBrief account.
/// </summary>
public sealed class MockFlightPlanProvider(
    IStoragePaths storagePaths,
    ILogger<MockFlightPlanProvider> logger) : IFlightPlanProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<MockFlightPlanProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProviderName => "Sample";

    /// <summary>
    /// The squawk pinned by the sample scenario, read alongside the plan.
    /// </summary>
    /// <remarks>
    /// Exposed separately rather than on <see cref="FlightPlan"/> because a transponder code is
    /// something ATC assigns, not something a flight plan carries. The sample file pins it only
    /// so the demonstration and the end-to-end test are reproducible.
    /// </remarks>
    public SquawkCode? PreassignedSquawk { get; private set; }

    /// <inheritdoc />
    public async Task<FlightPlanImportResult> ImportLatestAsync(
        FlightPlanImportRequest request,
        CancellationToken cancellationToken)
    {
        // STORMATC_SAMPLE_FLIGHT picks another bundled demonstration flight, for example
        // "STORM456-STMK-STMT"; the default stays the original STORM123 flight.
        string requested = Environment.GetEnvironmentVariable("STORMATC_SAMPLE_FLIGHT") ?? string.Empty;
        string fileName = requested.Length > 0 && requested.All(c => char.IsLetterOrDigit(c) || c == '-')
            ? requested + ".json"
            : "STORM123-STMR-STMD.json";
        string path = Path.Combine(_storagePaths.DataDirectory, "sample-flights", fileName);

        if (!File.Exists(path))
        {
            return FlightPlanImportResult.Fail(
                $"The sample flight plan was not found at {path}.",
                "Reinstall StormATC or restore the data/sample-flights directory.");
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            using (document)
            {
                (FlightPlan plan, SquawkCode? squawk) = ParseSampleFlight(document.RootElement);
                PreassignedSquawk = squawk;

                _logger.LogInformation("Loaded the sample flight plan {Summary}.", plan.ToSummary());

                return FlightPlanImportResult.Ok(plan, ["This is fictional sample data for demonstration only."]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            _logger.LogError(ex, "The sample flight plan could not be parsed.");
            return FlightPlanImportResult.Fail(
                "The sample flight plan file is invalid.",
                "Restore data/sample-flights/STORM123-STMR-STMD.json from the repository.");
        }
    }

    private static (FlightPlan Plan, SquawkCode? Squawk) ParseSampleFlight(JsonElement root)
    {
        string callsignText = root.GetProperty("callsign").GetString() ?? "STORM123";

        RunwayIdentifier? departureRunway = null;
        if (root.TryGetProperty("departureRunway", out JsonElement depRunway)
            && depRunway.ValueKind == JsonValueKind.String
            && RunwayIdentifier.TryCreate(depRunway.GetString(), out RunwayIdentifier parsedDeparture))
        {
            departureRunway = parsedDeparture;
        }

        RunwayIdentifier? arrivalRunway = null;
        if (root.TryGetProperty("arrivalRunway", out JsonElement arrRunway)
            && arrRunway.ValueKind == JsonValueKind.String
            && RunwayIdentifier.TryCreate(arrRunway.GetString(), out RunwayIdentifier parsedArrival))
        {
            arrivalRunway = parsedArrival;
        }

        Altitude? cruise = null;
        if (root.TryGetProperty("cruiseAltitudeFlightLevel", out JsonElement fl)
            && fl.ValueKind == JsonValueKind.Number)
        {
            cruise = Altitude.FromFlightLevel(fl.GetInt32());
        }

        SquawkCode? squawk = null;
        if (root.TryGetProperty("preassignedSquawk", out JsonElement squawkElement)
            && squawkElement.ValueKind == JsonValueKind.String
            && SquawkCode.TryCreate(squawkElement.GetString(), out SquawkCode parsedSquawk))
        {
            squawk = parsedSquawk;
        }

        var legs = new List<RouteLeg>();
        if (root.TryGetProperty("routeLegs", out JsonElement legsElement)
            && legsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement legElement in legsElement.EnumerateArray())
            {
                if (!legElement.TryGetProperty("identifier", out JsonElement idElement)
                    || idElement.GetString() is not { Length: > 0 } identifier)
                {
                    continue;
                }

                GeoPosition? position = null;
                if (legElement.TryGetProperty("position", out JsonElement posElement)
                    && posElement.ValueKind == JsonValueKind.Object
                    && posElement.TryGetProperty("latitude", out JsonElement latElement)
                    && posElement.TryGetProperty("longitude", out JsonElement lonElement)
                    && GeoPosition.TryCreate(latElement.GetDouble(), lonElement.GetDouble(), out GeoPosition parsed))
                {
                    position = parsed;
                }

                Altitude? plannedAltitude = null;
                if (legElement.TryGetProperty("plannedAltitudeFeet", out JsonElement altElement)
                    && altElement.ValueKind == JsonValueKind.Number
                    && Altitude.TryFromFeet(altElement.GetInt32(), out Altitude altitude))
                {
                    plannedAltitude = altitude;
                }

                legs.Add(new RouteLeg
                {
                    Identifier = identifier,
                    Via = ReadOptionalString(legElement, "via"),
                    Position = position,
                    PlannedAltitude = plannedAltitude,
                    CumulativeDistanceNauticalMiles =
                        legElement.TryGetProperty("cumulativeDistanceNauticalMiles", out JsonElement distElement)
                        && distElement.ValueKind == JsonValueKind.Number
                            ? distElement.GetDouble()
                            : null,
                    Stage = ReadOptionalString(legElement, "stage"),
                });
            }
        }

        var alternates = new List<string>();
        if (root.TryGetProperty("alternateIcaos", out JsonElement altsElement)
            && altsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in altsElement.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } icao)
                {
                    alternates.Add(icao);
                }
            }
        }

        var plan = new FlightPlan
        {
            FlightPlanId = ReadOptionalString(root, "flightPlanId") ?? "sample",
            Callsign = Callsign.Create(callsignText),
            FlightNumber = ReadOptionalString(root, "flightNumber"),
            AircraftIcaoType = ReadOptionalString(root, "aircraftIcaoType"),
            Registration = ReadOptionalString(root, "registration"),
            DepartureIcao = root.GetProperty("departureIcao").GetString() ?? "STMR",
            DestinationIcao = root.GetProperty("destinationIcao").GetString() ?? "STMD",
            AlternateIcaos = alternates,
            DepartureRunway = departureRunway,
            ArrivalRunway = arrivalRunway,
            StandardInstrumentDeparture = ReadOptionalString(root, "standardInstrumentDeparture"),
            StandardTerminalArrival = ReadOptionalString(root, "standardTerminalArrival"),
            Approach = ReadOptionalString(root, "approach"),
            Route = ReadOptionalString(root, "route"),
            RouteLegs = legs,
            CruiseAltitude = cruise,
            EstimatedBlockTime = root.TryGetProperty("estimatedBlockTimeMinutes", out JsonElement blockElement)
                && blockElement.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromMinutes(blockElement.GetInt32())
                    : null,
            PlannedBlockFuelKilograms = ReadOptionalDouble(root, "plannedBlockFuelKilograms"),
            PlannedTripFuelKilograms = ReadOptionalDouble(root, "plannedTripFuelKilograms"),

            // The sample plan is always "just generated" so it never trips the staleness warning.
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceIdentifier = "Sample",
        };

        return (plan, squawk);

        static string? ReadOptionalString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static double? ReadOptionalDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
    }
}

/// <summary>
/// Supplies simulated traffic for the runway-occupancy model.
/// </summary>
/// <remarks>
/// The prototype's traffic model is deliberately minimal: it exists so the engine's
/// "runway occupied" refusal path is exercisable, not to simulate a busy aerodrome. Traffic is
/// injected by the developer controls rather than generated, which keeps scenarios reproducible.
/// </remarks>
public sealed class MockTrafficProvider : ITrafficProvider
{
    private readonly List<TrafficContact> _traffic = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<TrafficContact>> GetRelevantTrafficAsync(
        AircraftState aircraftState,
        CancellationToken cancellationToken,
        double radiusNauticalMiles = 15)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TrafficContact>>([.. _traffic]);
        }
    }

    /// <summary>Places traffic on a runway, so the engine will refuse a takeoff or landing clearance.</summary>
    public void OccupyRunway(RunwayIdentifier runway, GeoPosition position, string callsign = "MOCK001")
    {
        lock (_lock)
        {
            _traffic.RemoveAll(t => t.OccupiedRunway == runway);
            _traffic.Add(new TrafficContact
            {
                Callsign = callsign,
                Position = position,
                AltitudeFeet = 0,
                GroundSpeedKnots = 0,
                IsOnRunway = true,
                OccupiedRunway = runway,
            });
        }
    }

    /// <summary>Removes all traffic.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _traffic.Clear();
        }
    }
}

/// <summary>
/// Supplies fixed weather for the sample aerodromes.
/// </summary>
/// <remarks>
/// <para>
/// Returns a real observation only for the bundled fictional aerodromes. For anything else it
/// returns null, and that null is load-bearing: it tells the engine there is no weather source,
/// so wind and QNH are omitted from clearances rather than invented. A mock that made up
/// plausible weather for every aerodrome would defeat the whole "never invent facts" rule.
/// </para>
/// </remarks>
public sealed class MockWeatherProvider : IWeatherProvider
{
    private static readonly Dictionary<string, WeatherSnapshot> Observations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Wind favours runway 18 at STMR and runway 36 at STMD, matching the sample plan.
            ["STMR"] = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(190),
                WindSpeedKnots = 8,
                QnhHectopascals = 1013,
                VisibilityMeters = 10000,
                Source = "Mock (fictional)",
            },
            ["STMK"] = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(270),
                WindSpeedKnots = 10,
                QnhHectopascals = 1012,
                VisibilityMeters = 9000,
                Source = "Mock (fictional)",
            },
            ["STMT"] = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(260),
                WindSpeedKnots = 14,
                QnhHectopascals = 1009,
                VisibilityMeters = 8000,
                Source = "Mock (fictional)",
            },
            ["STMD"] = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(350),
                WindSpeedKnots = 6,
                QnhHectopascals = 1015,
                VisibilityMeters = 10000,
                Source = "Mock (fictional)",
            },
        };

    /// <inheritdoc />
    public Task<WeatherSnapshot?> GetAirportWeatherAsync(string icao, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(icao))
        {
            return Task.FromResult<WeatherSnapshot?>(null);
        }

        return Task.FromResult(Observations.TryGetValue(icao, out WeatherSnapshot? snapshot)
            ? snapshot
            : null);
    }
}

/// <summary>
/// Supplies sector definitions from the bundled fictional airspace file.
/// </summary>
public sealed class JsonAirspaceDataProvider(
    IStoragePaths storagePaths,
    ILogger<JsonAirspaceDataProvider> logger) : IAirspaceDataProvider
{
    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<JsonAirspaceDataProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private IReadOnlyList<SectorDefinition>? _cached;

    /// <inheritdoc />
    public async Task<SectorDefinition?> FindSectorAsync(
        GeoPosition position,
        double altitudeFeet,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SectorDefinition> sectors = await ListSectorsAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (SectorDefinition sector in sectors)
        {
            if (sector.Contains(position, altitudeFeet))
            {
                return sector;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SectorDefinition>> ListSectorsAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string directory = Path.Combine(_storagePaths.DataDirectory, "sample-sectors");
        var sectors = new List<SectorDefinition>();

        if (!Directory.Exists(directory))
        {
            _cached = sectors;
            return sectors;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                await using FileStream stream = File.OpenRead(file);
                JsonDocument document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("sectors", out JsonElement sectorsElement)
                        || sectorsElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement element in sectorsElement.EnumerateArray())
                    {
                        SectorDefinition? sector = ParseSector(element);
                        if (sector is not null)
                        {
                            sectors.Add(sector);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogError(ex, "Sector file {File} is invalid and was skipped.", file);
            }
        }

        _cached = sectors;
        return sectors;
    }

    private static SectorDefinition? ParseSector(JsonElement element)
    {
        if (!element.TryGetProperty("sectorId", out JsonElement idElement)
            || idElement.GetString() is not { Length: > 0 } sectorId
            || !element.TryGetProperty("frequency", out JsonElement freqElement)
            || !freqElement.TryGetDecimal(out decimal megahertz)
            || !Frequency.TryFromMegahertz(megahertz, out Frequency frequency))
        {
            return null;
        }

        return new SectorDefinition
        {
            SectorId = sectorId,
            ControllerName = element.TryGetProperty("controllerName", out JsonElement nameElement)
                ? nameElement.GetString() ?? sectorId
                : sectorId,
            Frequency = frequency,
            MinimumLatitude = ReadDouble(element, "minimumLatitude"),
            MaximumLatitude = ReadDouble(element, "maximumLatitude"),
            MinimumLongitude = ReadDouble(element, "minimumLongitude"),
            MaximumLongitude = ReadDouble(element, "maximumLongitude"),
            MinimumAltitudeFeet = (int)ReadDouble(element, "minimumAltitudeFeet"),
            MaximumAltitudeFeet = (int)ReadDouble(element, "maximumAltitudeFeet"),
            Position = element.TryGetProperty("position", out JsonElement posElement)
                && posElement.GetString() == "Approach"
                    ? Domain.ControllerPosition.Approach
                    : Domain.ControllerPosition.Center,
        };

        static double ReadDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;
    }
}
