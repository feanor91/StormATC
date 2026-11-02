using System.Globalization;
using System.Text.Json;

namespace StormATC.Application.Map;

/// <summary>Reads the airspaces of the OpenAIP API (<c>/api/airspaces</c>) into <see cref="MapAirspace"/> and decides which families to show.</summary>
public static class OpenAipAirspaces
{
    private static readonly string[] Classes = ["A", "B", "C", "D", "E", "F", "G"];

    /// <summary>The family of an OpenAIP <c>type</c>: it decides the colour and the filter.</summary>
    /// <param name="type">The OpenAIP type code.</param>
    public static string KindFromType(int type) => type switch
    {
        1 or 2 or 3 or 8 or 9 or 17 or 18 or 19 or 29 or 31 => "restricted",
        4 or 36 => "ctr",
        7 or 24 or 25 or 26 or 34 or 35 => "tma",
        5 or 6 or 13 or 14 or 20 or 22 or 23 => "radio",
        21 or 28 => "leisure",
        10 or 11 or 12 or 15 or 16 or 27 or 30 or 32 or 33 => "sector",
        _ => "other",
    };

    /// <summary>The name of an OpenAIP <c>type</c>.</summary>
    /// <param name="type">The OpenAIP type code.</param>
    public static string TypeName(int type) => type switch
    {
        1 => "Zone réglementée (R)",
        2 => "Zone dangereuse (D)",
        3 => "Zone interdite (P)",
        4 => "CTR",
        5 => "TMZ",
        6 => "RMZ",
        7 => "TMA",
        8 => "TRA",
        9 => "TSA",
        10 => "FIR",
        11 => "UIR",
        12 => "ADIZ",
        13 => "ATZ",
        14 => "MATZ",
        15 => "Voie aérienne",
        16 => "MTR",
        17 => "Zone d'alerte",
        18 => "Zone d'avertissement",
        19 => "Zone protégée",
        20 => "HTZ",
        21 => "Secteur planeur",
        22 => "TRP",
        23 => "TIZ",
        24 => "TIA",
        25 => "MTA",
        26 => "CTA",
        27 => "Secteur ACC",
        28 => "Activité aérienne sportive",
        29 => "Restriction de survol à basse altitude",
        30 => "MRT",
        31 => "TFR",
        32 => "Secteur VFR",
        33 => "Secteur FIS",
        34 => "LTA",
        35 => "UTA",
        36 => "MCTR",
        _ => "Espace aérien",
    };

    /// <summary>
    /// A limit of OpenAIP: unit 0 is metres, 1 feet, 6 flight level; reference 0 is the ground, 1 the sea level, 2 standard.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="unit">The unit code.</param>
    /// <param name="referenceDatum">The reference code.</param>
    public static (string Text, double Feet) FormatLimit(int value, int unit, int referenceDatum)
    {
        if (unit == 6 || referenceDatum == 2)
        {
            return ("FL" + value.ToString(CultureInfo.InvariantCulture), value * 100.0);
        }

        if (value == 0 && referenceDatum == 0)
        {
            return ("SFC", 0);
        }

        double feet = unit == 0 ? value * 3.28084 : value;
        string suffix = referenceDatum == 0 ? " GND" : " AMSL";
        string number = value.ToString(CultureInfo.InvariantCulture);
        return (unit == 0 ? $"{number}m{suffix}" : $"{number}ft{suffix}", feet);
    }

    /// <summary>Whether a family is shown, with the user's filter (sectors and the rest are hidden by default).</summary>
    /// <param name="airspace">The airspace.</param>
    /// <param name="maxFloorFeet">Airspaces whose floor is higher than this are hidden; null shows them all.</param>
    public static bool IsShown(MapAirspace airspace, double? maxFloorFeet)
    {
        ArgumentNullException.ThrowIfNull(airspace);
        if (maxFloorFeet is { } max && airspace.LowerFt > max)
        {
            return false;
        }

        return airspace.Kind is "restricted" or "ctr" or "tma" or "radio" or "leisure";
    }

    /// <summary>Reads one page of the API's answer; the airspaces without a polygon are left out.</summary>
    /// <param name="page">The JSON document of one page.</param>
    /// <param name="totalPages">The number of pages, as the API says it.</param>
    public static List<MapAirspace> ParsePage(JsonElement page, out int totalPages)
    {
        totalPages = page.TryGetProperty("totalPages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Number ? pages.GetInt32() : 1;
        var result = new List<MapAirspace>();
        if (page.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (TryParse(item) is { } airspace)
                {
                    result.Add(airspace);
                }
            }
        }

        return result;
    }

    private static MapAirspace? TryParse(JsonElement e)
    {
        if (!e.TryGetProperty("geometry", out JsonElement geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rings = new List<double[][]>();
        string type = geometry.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        if (!geometry.TryGetProperty("coordinates", out JsonElement coordinates))
        {
            return null;
        }

        if (type == "Polygon")
        {
            AddOuterRing(coordinates, rings);
        }
        else if (type == "MultiPolygon")
        {
            foreach (JsonElement polygon in coordinates.EnumerateArray())
            {
                AddOuterRing(polygon, rings);
            }
        }
        else
        {
            return null;
        }

        if (rings.Count == 0)
        {
            return null;
        }

        int icaoClass = e.TryGetProperty("icaoClass", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        int typeCode = e.TryGetProperty("type", out JsonElement tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
        (string lowerText, double lowerFeet) = Limit(e, "lowerLimit", 0);
        (string upperText, double upperFeet) = Limit(e, "upperLimit", 1e9);
        return new MapAirspace(
            e.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty,
            icaoClass >= 0 && icaoClass < Classes.Length ? Classes[icaoClass] : string.Empty,
            TypeName(typeCode),
            KindFromType(typeCode),
            lowerText,
            upperText,
            lowerFeet,
            Math.Min(upperFeet, 1e9),
            [.. rings]);
    }

    private static void AddOuterRing(JsonElement polygon, List<double[][]> rings)
    {
        foreach (JsonElement outer in polygon.EnumerateArray())
        {
            var points = new List<double[]>();
            foreach (JsonElement point in outer.EnumerateArray())
            {
                points.Add([Math.Round(point[0].GetDouble(), 4), Math.Round(point[1].GetDouble(), 4)]);
            }

            if (points.Count >= 4)
            {
                rings.Add([.. points]);
            }

            return; // the first ring is the outer one; holes are ignored
        }
    }

    private static (string Text, double Feet) Limit(JsonElement e, string name, double missingFeet)
    {
        if (!e.TryGetProperty(name, out JsonElement limit) || limit.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, missingFeet);
        }

        int value = limit.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        int unit = limit.TryGetProperty("unit", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 1;
        int datum = limit.TryGetProperty("referenceDatum", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
        return FormatLimit(value, unit, datum);
    }
}
