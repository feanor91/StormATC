using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.FlightPlanning;

/// <summary>A flight plan as the simulator saves it in a <c>.PLN</c> file.</summary>
/// <param name="Title">The plan's title.</param>
/// <param name="DepartureIcao">The departure aerodrome.</param>
/// <param name="DestinationIcao">The destination aerodrome (the departure for a local flight).</param>
/// <param name="IsVfr">True when the plan's type is VFR.</param>
/// <param name="CruisingAltitudeFeet">The planned cruising altitude, when the file gives one.</param>
/// <param name="DepartureRunway">The departure runway, such as <c>08</c> or <c>26L</c>, when the file gives one.</param>
/// <param name="Waypoints">The waypoints between the two aerodromes, in order.</param>
/// <param name="ArrivalRunway">The runway planned at the destination, such as <c>33</c>, when the plan gives one.</param>
public sealed record SimFlightPlan(
    string Title,
    string DepartureIcao,
    string DestinationIcao,
    bool IsVfr,
    int? CruisingAltitudeFeet,
    string? DepartureRunway,
    IReadOnlyList<SimFlightPlanWaypoint> Waypoints,
    string? ArrivalRunway = null);

/// <summary>A waypoint of a simulator flight plan.</summary>
/// <param name="Identifier">The waypoint's identifier.</param>
/// <param name="Position">Where it is, when the file gives a readable position.</param>
public sealed record SimFlightPlanWaypoint(string Identifier, GeoPosition? Position);

/// <summary>Reads the <c>.PLN</c> files Microsoft Flight Simulator writes.</summary>
public static class PlnFlightPlanParser
{
    /// <summary>Parses the text of a <c>.PLN</c> file.</summary>
    /// <param name="xml">The file's text.</param>
    /// <param name="plan">The plan, when the file holds one.</param>
    public static bool TryParse(string? xml, [NotNullWhen(true)] out SimFlightPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        XElement? root;
        try
        {
            root = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName is "FlightPlan.FlightPlan" or "FlightPlan");
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        string departure = Icao(Text(root, "DepartureID"));
        string destination = Icao(Text(root, "DestinationID"));
        if (departure.Length == 0)
        {
            return false;
        }

        // A plan with no usable destination is a local flight from the departure aerodrome.
        // MSFS may also write a name that is not an aerodrome here, so only a code of four
        // letters or digits counts.
        if (destination.Length != 4)
        {
            destination = departure;
        }

        int? cruise = double.TryParse(
            Text(root, "CruisingAlt"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double feet) && feet > 0
            ? (int)Math.Round(feet)
            : null;

        List<SimFlightPlanWaypoint> waypoints = [];
        foreach (XElement waypoint in root.Elements().Where(e => e.Name.LocalName == "ATCWaypoint"))
        {
            string id = (waypoint.Attribute("id")?.Value ?? string.Empty).Trim();
            if (id.Length == 0 || id.Equals(departure, StringComparison.OrdinalIgnoreCase)
                || id.Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            waypoints.Add(new SimFlightPlanWaypoint(id, ParsePosition(Text(waypoint, "WorldPosition"))));
        }

        plan = new SimFlightPlan(
            Text(root, "Title"),
            departure,
            destination,
            string.Equals(Text(root, "FPType"), "VFR", StringComparison.OrdinalIgnoreCase),
            cruise,
            ParseRunway(root, "DepartureDetails"),
            waypoints,
            ParseRunway(root, "ArrivalDetails"));
        return true;
    }

    /// <summary>Reads a position written as <c>N48°49'31.80",E2°37'30.00",+000290.00</c>.</summary>
    internal static GeoPosition? ParsePosition(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || ParseCoordinate(parts[0]) is not double latitude
            || ParseCoordinate(parts[1]) is not double longitude
            || !GeoPosition.TryCreate(latitude, longitude, out GeoPosition position))
        {
            return null;
        }

        return position;
    }

    private static double? ParseCoordinate(string text)
    {
        if (text.Length < 2 || "NSEW".IndexOf(char.ToUpperInvariant(text[0]), StringComparison.Ordinal) < 0)
        {
            return null;
        }

        string[] numbers = text[1..].Split(['°', '\'', '"'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        double value = 0;
        double divisor = 1;
        foreach (string number in numbers.Take(3))
        {
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double part))
            {
                return null;
            }

            value += part / divisor;
            divisor *= 60;
        }

        char hemisphere = char.ToUpperInvariant(text[0]);
        return hemisphere is 'S' or 'W' ? -value : value;
    }

    private static string? ParseRunway(XElement root, string detailsName)
    {
        XElement? details = root.Elements().FirstOrDefault(e => e.Name.LocalName == detailsName);
        string number = details is null ? string.Empty : Text(details, "RunwayNumberFP");
        if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 36)
        {
            return null;
        }

        string designator = (details is null ? string.Empty : Text(details, "RunwayDesignatorFP")).Trim().ToUpperInvariant();
        string suffix = designator switch
        {
            "LEFT" or "L" => "L",
            "RIGHT" or "R" => "R",
            "CENTER" or "C" => "C",
            _ => string.Empty,
        };

        return value.ToString("00", CultureInfo.InvariantCulture) + suffix;
    }

    private static string Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string Icao(string text) => text.Trim().ToUpperInvariant();
}

/// <summary>Turns a simulator plan into the plan StormATC works from.</summary>
public static class SimFlightPlanConverter
{
    /// <summary>Builds the plan, or says that the aircraft has no usable callsign.</summary>
    /// <param name="plan">The plan read from the simulator.</param>
    /// <param name="callsign">The callsign or registration of the aircraft, such as F-HAST.</param>
    /// <param name="now">The time the plan is generated.</param>
    /// <param name="flightPlan">The plan, when the callsign is usable.</param>
    public static bool TryConvert(
        SimFlightPlan plan,
        string? callsign,
        DateTimeOffset now,
        [NotNullWhen(true)] out FlightPlan? flightPlan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!Callsign.TryCreate(callsign, out Callsign? parsed))
        {
            flightPlan = null;
            return false;
        }

        RunwayIdentifier? runway = RunwayIdentifier.TryCreate(plan.DepartureRunway, out RunwayIdentifier parsedRunway)
            ? parsedRunway
            : null;

        flightPlan = new FlightPlan
        {
            FlightPlanId = string.Create(CultureInfo.InvariantCulture, $"sim-{plan.DepartureIcao}-{plan.DestinationIcao}"),
            Callsign = parsed,
            Registration = parsed.Original,
            DepartureIcao = plan.DepartureIcao,
            DestinationIcao = plan.DestinationIcao,
            DepartureRunway = runway,
            ArrivalRunway = RunwayIdentifier.TryCreate(plan.ArrivalRunway, out RunwayIdentifier arrival) ? arrival : null,
            Route = plan.Waypoints.Count == 0 ? null : string.Join(' ', plan.Waypoints.Select(w => w.Identifier)),
            RouteLegs =
            [
                .. plan.Waypoints.Select(waypoint => new RouteLeg
                {
                    Identifier = waypoint.Identifier,
                    Position = waypoint.Position,
                }),
            ],
            // The simulator writes 45,000 ft in a plan whose altitude was never set; VFR stays below
            // flight level 180, so a VFR plan above that has no altitude to speak of.
            CruiseAltitude = plan.CruisingAltitudeFeet is int feet
                && !(plan.IsVfr && feet > 18000)
                && Altitude.TryFromFeet(feet, out Altitude altitude)
                ? altitude
                : null,
            GeneratedAt = now,
            SourceIdentifier = "Simulator",
        };
        return true;
    }
}

/// <summary>Finds the simulator's flight plan files.</summary>
public static class SimFlightPlanLocator
{
    /// <summary>
    /// The folders where MSFS 2024 writes the plan of the flight that was just started: the
    /// Microsoft Store and the Steam editions.
    /// </summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return
        [
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState", "MISSIONS", "Custom", "CustomFlight"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "MISSIONS", "Custom", "CustomFlight"),
        ];
    }

    /// <summary>
    /// Where a pilot is likely to keep a plan saved from the EFB or a planner: the desktop and the
    /// documents folder. Only used when the pilot asks for an import.
    /// </summary>
    public static IReadOnlyList<string> UserFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    ];

    /// <summary>The most recently written <c>.PLN</c> in the folders, or null.</summary>
    /// <param name="folders">The folders to look in.</param>
    public static string? FindNewest(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.pln", SearchOption.TopDirectoryOnly))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
