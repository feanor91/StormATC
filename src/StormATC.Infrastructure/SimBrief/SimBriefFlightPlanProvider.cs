using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.SimBrief;

/// <summary>
/// Imports the most recent SimBrief operational flight plan.
/// </summary>
/// <remarks>
/// <para>
/// The SimBrief user ID is an account number, not a credential — it is passed as a query
/// parameter exactly as SimBrief's own documentation describes. No API key is involved and
/// nothing secret is transmitted.
/// </para>
/// <para>
/// Parsing is deliberately tolerant. SimBrief's XML varies with aircraft, airline profile, and
/// options, and fields appear and disappear between plans. Everything optional is read
/// defensively, unknown elements are ignored, and a missing field produces a warning rather than
/// a failed import.
/// </para>
/// </remarks>
public sealed class SimBriefFlightPlanProvider(
    HttpClient httpClient,
    IOptions<SimBriefOptions> options,
    IStoragePaths storagePaths,
    ILogger<SimBriefFlightPlanProvider> logger) : IFlightPlanProvider
{
    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly SimBriefOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<SimBriefFlightPlanProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProviderName => "SimBrief";

    /// <inheritdoc />
    public async Task<FlightPlanImportResult> ImportLatestAsync(
        FlightPlanImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? userId = request.UserId ?? _options.UserId;

        if (!IsValidUserId(userId))
        {
            return FlightPlanImportResult.Fail(
                "No valid SimBrief user ID is configured.",
                "Enter your numeric SimBrief user ID (or your SimBrief username) on the Flight Plan page.");
        }

        if (!ProviderEndpointPolicy.IsAllowedSimBrief(_options.BaseUrl))
        {
            return FlightPlanImportResult.Fail(
                "The configured SimBrief endpoint was blocked.",
                "Restore the official SimBrief endpoint in appsettings.json.");
        }

        var uri = new Uri(
            $"{_options.BaseUrl}?{BuildUserIdParameter(userId!)}",
            UriKind.Absolute);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.Timeout);

        string xml;
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(uri, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpFailure(response.StatusCode);
            }

            xml = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FlightPlanImportResult.Fail(
                $"SimBrief did not respond within {_options.Timeout.TotalSeconds:F0} seconds.",
                "Check your internet connection and try importing again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "The SimBrief request failed.");
            return FlightPlanImportResult.Fail(
                "SimBrief could not be reached.",
                "Check your internet connection, then use Re-import on the Flight Plan page.");
        }

        string? cachePath = null;
        if (_options.CacheRawResponses)
        {
            cachePath = await CacheRawResponseAsync(xml, cancellationToken).ConfigureAwait(false);
        }

        return ParseOfp(xml, cachePath);
    }

    /// <summary>
    /// Parses a SimBrief OFP document.
    /// </summary>
    /// <remarks>
    /// Exposed as a static method so the infrastructure tests can run it against fixture XML
    /// with no HTTP involved.
    /// </remarks>
    public static FlightPlanImportResult ParseOfp(string xml, string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return FlightPlanImportResult.Fail(
                "SimBrief returned a response that is not valid XML.",
                "Regenerate the flight plan on simbrief.com, then import again.");
        }

        XElement? root = document.Root;
        if (root is null)
        {
            return FlightPlanImportResult.Fail(
                "The SimBrief response was empty.",
                "Generate a flight plan on simbrief.com, then import again.");
        }

        // SimBrief signals a failed fetch with a <fetch><status> element rather than an HTTP error.
        string? fetchStatus = root.Element("fetch")?.Element("status")?.Value;
        if (fetchStatus is { Length: > 0 }
            && !fetchStatus.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            return FlightPlanImportResult.Fail(
                $"SimBrief reported: {fetchStatus}",
                "Verify your SimBrief user ID and that you have generated at least one flight plan.");
        }

        var warnings = new List<string>();

        XElement? general = root.Element("general");
        XElement? origin = root.Element("origin");
        XElement? destination = root.Element("destination");
        XElement? alternate = root.Element("alternate");
        XElement? aircraft = root.Element("aircraft");
        XElement? fuel = root.Element("fuel");
        XElement? times = root.Element("times");

        string? originIcao = Read(origin, "icao_code");
        string? destinationIcao = Read(destination, "icao_code");

        if (originIcao is null || destinationIcao is null)
        {
            return FlightPlanImportResult.Fail(
                "The SimBrief plan is missing its origin or destination aerodrome.",
                "Regenerate the flight plan on simbrief.com, then import again.");
        }

        // Callsign: SimBrief supplies ATC callsign, airline ICAO, and flight number separately,
        // and which are populated depends on the airline profile.
        string? atcCallsign = Read(general, "atc_callsign");
        string? airlineIcao = Read(general, "icao_airline");
        string? flightNumber = Read(general, "flight_number");

        string callsignText = atcCallsign
            ?? (airlineIcao is not null && flightNumber is not null
                ? airlineIcao + flightNumber
                : null)
            ?? Read(aircraft, "reg")
            ?? "UNKNOWN";

        if (!Callsign.TryCreate(callsignText, out Callsign? callsign))
        {
            return FlightPlanImportResult.Fail(
                $"The SimBrief plan contains an unusable callsign ('{callsignText}').",
                "Set an ATC callsign in your SimBrief airline profile, then regenerate the plan.");
        }

        RunwayIdentifier? departureRunway = ParseRunway(Read(origin, "plan_rwy"), warnings, "departure");
        RunwayIdentifier? arrivalRunway = ParseRunway(Read(destination, "plan_rwy"), warnings, "arrival");

        Altitude? cruise = null;
        if (int.TryParse(Read(general, "initial_altitude"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int cruiseFeet)
            && Altitude.TryFromFeet(cruiseFeet, out Altitude parsedCruise))
        {
            // SimBrief reports cruise in feet; above the transition it is spoken as a level.
            cruise = cruiseFeet >= 18000 ? parsedCruise.AsFlightLevel() : parsedCruise;
        }
        else
        {
            warnings.Add("The plan does not specify a cruise altitude.");
        }

        var alternates = new List<string>();
        if (Read(alternate, "icao_code") is { Length: > 0 } alternateIcao)
        {
            alternates.Add(alternateIcao);
        }

        List<RouteLeg> legs = ParseNavlog(root, warnings);

        var plan = new FlightPlan
        {
            FlightPlanId = Read(general, "request_id") ?? Guid.CreateVersion7().ToString("N"),
            Callsign = callsign,
            FlightNumber = flightNumber,
            AircraftIcaoType = Read(aircraft, "icaocode") ?? Read(aircraft, "icao_code"),
            Registration = Read(aircraft, "reg"),
            DepartureIcao = originIcao,
            DestinationIcao = destinationIcao,
            AlternateIcaos = alternates,
            DepartureRunway = departureRunway,
            ArrivalRunway = arrivalRunway,
            StandardInstrumentDeparture = NormalizeProcedure(Read(general, "sid_ident")),
            StandardTerminalArrival = NormalizeProcedure(Read(general, "star_ident")),
            Approach = null,
            Route = Read(general, "route"),
            RouteLegs = legs,
            CruiseAltitude = cruise,
            EstimatedBlockTime = ParseSeconds(Read(times, "est_block")),
            PlannedBlockFuelKilograms = ParseFuel(Read(fuel, "plan_ramp")),
            PlannedTripFuelKilograms = ParseFuel(Read(fuel, "enroute_burn")),
            GeneratedAt = ParseUnixSeconds(Read(params_(root), "time_generated")),
            SourceIdentifier = "SimBrief",
            RawSourceCachePath = cachePath,
        };

        return FlightPlanImportResult.Ok(plan, warnings);

        static XElement? params_(XElement root) => root.Element("params");
    }

    /// <summary>Parses the navlog into route legs.</summary>
    private static List<RouteLeg> ParseNavlog(XElement root, List<string> warnings)
    {
        var legs = new List<RouteLeg>();

        XElement? navlog = root.Element("navlog");
        if (navlog is null)
        {
            warnings.Add("The plan contains no navlog, so route legs are unavailable.");
            return legs;
        }

        foreach (XElement fix in navlog.Elements("fix"))
        {
            string? identifier = Read(fix, "ident");
            if (identifier is not { Length: > 0 })
            {
                continue;
            }

            GeoPosition? position = null;
            if (double.TryParse(Read(fix, "pos_lat"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double latitude)
                && double.TryParse(Read(fix, "pos_long"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double longitude)
                && GeoPosition.TryCreate(latitude, longitude, out GeoPosition parsed))
            {
                position = parsed;
            }

            Altitude? plannedAltitude = null;
            if (int.TryParse(Read(fix, "altitude_feet"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int feet)
                && Altitude.TryFromFeet(feet, out Altitude altitude))
            {
                plannedAltitude = altitude;
            }

            double? cumulativeDistance = null;
            if (double.TryParse(Read(fix, "distance"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double distance))
            {
                cumulativeDistance = distance;
            }

            legs.Add(new RouteLeg
            {
                Identifier = identifier,
                Via = Read(fix, "via_airway"),
                Position = position,
                PlannedAltitude = plannedAltitude,
                CumulativeDistanceNauticalMiles = cumulativeDistance,
                Stage = Read(fix, "stage"),
            });
        }

        return legs;
    }

    private async Task<string?> CacheRawResponseAsync(string xml, CancellationToken cancellationToken)
    {
        try
        {
            _storagePaths.EnsureCreated();

            string path = Path.Combine(
                _storagePaths.FlightPlanCacheDirectory,
                $"simbrief-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.xml");

            await File.WriteAllTextAsync(path, xml, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            // Caching is a diagnostic nicety; failing to cache must not fail the import.
            _logger.LogWarning(ex, "Could not cache the raw SimBrief response.");
            return null;
        }
    }

    private static FlightPlanImportResult MapHttpFailure(System.Net.HttpStatusCode statusCode) =>
        statusCode switch
        {
            System.Net.HttpStatusCode.NotFound => FlightPlanImportResult.Fail(
                "SimBrief has no flight plan for that user ID.",
                "Generate a flight plan on simbrief.com, then import again."),

            System.Net.HttpStatusCode.TooManyRequests => FlightPlanImportResult.Fail(
                "SimBrief is rate-limiting requests.",
                "Wait a minute, then use Re-import on the Flight Plan page."),

            _ => FlightPlanImportResult.Fail(
                $"SimBrief returned HTTP {(int)statusCode}.",
                "Try importing again shortly. If it persists, check simbrief.com is reachable."),
        };

    /// <summary>
    /// Validates the user ID before it is put in a URL.
    /// </summary>
    /// <remarks>
    /// SimBrief accepts either a numeric ID or an alphanumeric username. Restricting the
    /// character set here also means the value can never inject anything into the query string.
    /// </remarks>
    private static bool IsValidUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 64)
        {
            return false;
        }

        foreach (char c in userId)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Chooses the right query parameter: numeric IDs use userid, names use username.</summary>
    private static string BuildUserIdParameter(string userId) =>
        userId.All(char.IsDigit)
            ? $"userid={Uri.EscapeDataString(userId)}"
            : $"username={Uri.EscapeDataString(userId)}";

    private static string? Read(XElement? parent, string elementName)
    {
        string? value = parent?.Element(elementName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static RunwayIdentifier? ParseRunway(string? value, List<string> warnings, string role)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        if (RunwayIdentifier.TryCreate(value, out RunwayIdentifier runway))
        {
            return runway;
        }

        warnings.Add($"The planned {role} runway '{value}' could not be interpreted and was ignored.");
        return null;
    }

    /// <summary>Normalises a SimBrief procedure identifier, treating its placeholders as absent.</summary>
    private static string? NormalizeProcedure(string? value) =>
        value is null || value.Equals("DCT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;

    private static TimeSpan? ParseSeconds(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static double? ParseFuel(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fuel)
            ? fuel
            : null;

    private static DateTimeOffset? ParseUnixSeconds(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
