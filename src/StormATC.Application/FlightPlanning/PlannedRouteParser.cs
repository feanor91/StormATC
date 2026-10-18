using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.FlightPlanning;

/// <summary>
/// Reads the flight plan route the simulator hands to JavaScript (<c>JS_FlightPlanRoute</c>):
/// what the EFB shows, what the pilot sent to ATC, and what was sent to the avionics.
/// </summary>
/// <remarks>
/// The simulator's in-game panel reads the route through the Planned Route listener and posts it
/// to StormATC. The shape follows the simulator's own JavaScript library: airports are ICAO
/// values (<c>type</c>, <c>region</c>, <c>airport</c>, <c>ident</c>), the runway has a
/// <c>number</c> and a <c>designator</c>, the cruise altitude is a number with an
/// <c>isFlightLevel</c> flag, and <c>enroute</c> lists the legs between the aerodromes.
/// </remarks>
public static class PlannedRouteParser
{
    /// <summary>Parses the route's JSON.</summary>
    /// <param name="json">The route, as the panel read it.</param>
    /// <param name="plan">The plan, when the route has a departure aerodrome.</param>
    public static bool TryParse(string? json, [NotNullWhen(true)] out SimFlightPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return TryParse(document.RootElement, out plan);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses the route from a JSON element.</summary>
    /// <param name="route">The route.</param>
    /// <param name="plan">The plan, when the route has a departure aerodrome.</param>
    public static bool TryParse(JsonElement route, [NotNullWhen(true)] out SimFlightPlan? plan)
    {
        plan = null;
        if (route.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string departure = Aerodrome(route, "departureAirport");
        if (departure.Length == 0)
        {
            return false;
        }

        string destination = Aerodrome(route, "destinationAirport");
        if (destination.Length != 4)
        {
            // No destination yet: a local flight from the departure aerodrome.
            destination = departure;
        }

        List<SimFlightPlanWaypoint> waypoints = [];
        if (route.TryGetProperty("enroute", out JsonElement legs) && legs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement leg in legs.EnumerateArray())
            {
                if (leg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string ident = Ident(leg, "fixIcao");
                if (ident.Length == 0 && leg.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    ident = (name.GetString() ?? string.Empty).Trim();
                }

                if (ident.Length == 0
                    || ident.Equals(departure, StringComparison.OrdinalIgnoreCase)
                    || ident.Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                GeoPosition? position = null;
                if (Boolean(leg, "hasLatLon")
                    && Number(leg, "lat") is double latitude
                    && Number(leg, "lon") is double longitude
                    && GeoPosition.TryCreate(latitude, longitude, out GeoPosition parsed))
                {
                    position = parsed;
                }

                waypoints.Add(new SimFlightPlanWaypoint(ident, position));
            }
        }

        bool vfr = Boolean(route, "isVfr");
        plan = new SimFlightPlan(
            $"{departure} - {destination}",
            departure,
            destination,
            vfr,
            CruiseAltitude(route),
            Runway(route, "departureRunway"),
            waypoints);
        return true;
    }

    /// <summary>The ICAO of an airport value: the ident, or else the airport it belongs to.</summary>
    private static string Aerodrome(JsonElement route, string name)
    {
        if (!route.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string ident = Text(value, "ident");
        return (ident.Length > 0 ? ident : Text(value, "airport")).ToUpperInvariant();
    }

    private static string Ident(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, "ident").ToUpperInvariant()
            : string.Empty;

    private static int? CruiseAltitude(JsonElement route)
    {
        if (!route.TryGetProperty("cruiseAltitude", out JsonElement altitude)
            || altitude.ValueKind != JsonValueKind.Object
            || Number(altitude, "altitude") is not double value
            || value <= 0)
        {
            return null;
        }

        // A flight level is given in hundreds of feet.
        return (int)Math.Round(Boolean(altitude, "isFlightLevel") ? value * 100 : value);
    }

    private static string? Runway(JsonElement route, string name)
    {
        if (!route.TryGetProperty(name, out JsonElement runway) || runway.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!int.TryParse(Text(runway, "number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || number is < 1 or > 36)
        {
            return null;
        }

        string suffix = Text(runway, "designator").ToUpperInvariant() switch
        {
            "L" or "LEFT" or "1" => "L",
            "R" or "RIGHT" or "2" => "R",
            "C" or "CENTER" or "CENTRE" or "3" => "C",
            _ => string.Empty,
        };

        return number.ToString("00", CultureInfo.InvariantCulture) + suffix;
    }

    private static string Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? string.Empty).Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool Boolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
