using System.Globalization;
using System.Text;

namespace StormATC.Application.Map;

/// <summary>The base maps and where their tiles come from.</summary>
public static class MapBaseMaps
{
    /// <summary>The base maps, in the order of the settings list.</summary>
    public static readonly IReadOnlyList<string> Keys = ["clair", "carto-light", "carto-voyager", "relief", "rues"];

    private const string EsriGray = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas";

    /// <summary>True for the maps that need a CARTO key.</summary>
    /// <param name="key">The base map.</param>
    public static bool NeedsCartoKey(string key) => key is "carto-light" or "carto-voyager";

    /// <summary>True when the map can be drawn: a CARTO map needs a key.</summary>
    /// <param name="key">The base map.</param>
    /// <param name="cartoKey">The CARTO key.</param>
    public static bool IsAvailable(string key, string cartoKey) => Keys.Contains(key) && (!NeedsCartoKey(key) || cartoKey.Length > 0);

    /// <summary>The map really drawn: a CARTO map without a key falls back to the light grey one, an unknown name too.</summary>
    /// <param name="key">The base map asked for.</param>
    /// <param name="cartoKey">The CARTO key, empty when there is none.</param>
    public static string Effective(string? key, string cartoKey) => key switch
    {
        "carto-light" or "carto-voyager" when cartoKey.Length > 0 => key,
        "relief" or "rues" => key,
        _ => "clair",
    };

    /// <summary>How many layers the map has (the light grey map has a layer of names over its base).</summary>
    /// <param name="key">The base map.</param>
    /// <param name="cartoKey">The CARTO key.</param>
    public static int LayerCount(string? key, string cartoKey) => Effective(key, cartoKey) == "clair" ? 2 : 1;

    /// <summary>The deepest tile zoom of the map.</summary>
    /// <param name="key">The base map.</param>
    /// <param name="cartoKey">The CARTO key.</param>
    public static int MaxZoom(string? key, string cartoKey) => Effective(key, cartoKey) switch
    {
        "carto-light" or "carto-voyager" or "rues" => 19,
        "relief" => 17,
        _ => 16,
    };

    /// <summary>The address of a tile, or null when the layer or the tile does not exist.</summary>
    /// <param name="key">The base map.</param>
    /// <param name="cartoKey">The CARTO key.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="z">The zoom.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    public static string? TileUrl(string? key, string cartoKey, int layer, int z, int x, int y)
    {
        if (layer < 0 || layer >= LayerCount(key, cartoKey) || z < 0 || z > MaxZoom(key, cartoKey))
        {
            return null;
        }

        int n = 1 << z;
        if (x < 0 || x >= n || y < 0 || y >= n)
        {
            return null;
        }

        string escaped = Uri.EscapeDataString(cartoKey);
        return Effective(key, cartoKey) switch
        {
            "carto-light" => $"https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png?key={escaped}",
            "carto-voyager" => $"https://basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png?key={escaped}",
            "relief" => $"https://{"abc"[(x + y) % 3]}.tile.opentopomap.org/{z}/{x}/{y}.png",
            "rues" => $"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            _ => $"{EsriGray}/{(layer == 0 ? "World_Light_Gray_Base" : "World_Light_Gray_Reference")}/MapServer/tile/{z}/{y}/{x}",
        };
    }
}

/// <summary>Reads the CSV files of OurAirports (aerodromes and beacons).</summary>
public static class OurAirportsData
{
    /// <summary>Where the aerodromes are published.</summary>
    public const string AirportsUrl = "https://davidmegginson.github.io/ourairports-data/airports.csv";

    /// <summary>Where the beacons are published.</summary>
    public const string NavaidsUrl = "https://davidmegginson.github.io/ourairports-data/navaids.csv";

    private static readonly HashSet<string> KeptTypes = ["large_airport", "medium_airport", "small_airport"];

    /// <summary>The aerodromes of a country (airports of every size, closed ones and heliports left out).</summary>
    /// <param name="reader">The CSV.</param>
    /// <param name="country">The ISO country code.</param>
    public static List<MapAirport> ParseAirports(TextReader reader, string country)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = new List<MapAirport>();
        string[]? header = null;
        int ident = 0, type = 0, name = 0, lat = 0, lon = 0, iso = 0;
        foreach (string[] row in Rows(reader))
        {
            if (header is null)
            {
                header = row;
                ident = Array.IndexOf(header, "ident");
                type = Array.IndexOf(header, "type");
                name = Array.IndexOf(header, "name");
                lat = Array.IndexOf(header, "latitude_deg");
                lon = Array.IndexOf(header, "longitude_deg");
                iso = Array.IndexOf(header, "iso_country");
                continue;
            }

            if (row.Length <= iso || !string.Equals(row[iso], country, StringComparison.OrdinalIgnoreCase) || !KeptTypes.Contains(row[type]))
            {
                continue;
            }

            if (double.TryParse(row[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
                && double.TryParse(row[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
            {
                result.Add(new MapAirport(row[ident], row[name], latitude, longitude));
            }
        }

        return result;
    }

    /// <summary>The VOR and NDB of a country (a DME or a TACAN alone is left out).</summary>
    /// <param name="reader">The CSV.</param>
    /// <param name="country">The ISO country code.</param>
    public static List<MapNavaid> ParseNavaids(TextReader reader, string country)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = new List<MapNavaid>();
        string[]? header = null;
        int ident = 0, name = 0, type = 0, freq = 0, lat = 0, lon = 0, iso = 0;
        foreach (string[] row in Rows(reader))
        {
            if (header is null)
            {
                header = row;
                ident = Array.IndexOf(header, "ident");
                name = Array.IndexOf(header, "name");
                type = Array.IndexOf(header, "type");
                freq = Array.IndexOf(header, "frequency_khz");
                lat = Array.IndexOf(header, "latitude_deg");
                lon = Array.IndexOf(header, "longitude_deg");
                iso = Array.IndexOf(header, "iso_country");
                continue;
            }

            if (row.Length <= iso || !string.Equals(row[iso], country, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? kind = row[type] switch
            {
                "VOR" or "VOR-DME" or "VORTAC" => "vor",
                "NDB" or "NDB-DME" => "ndb",
                _ => null,
            };
            if (kind is not null
                && double.TryParse(row[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
                && double.TryParse(row[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
                && double.TryParse(row[freq], NumberStyles.Float, CultureInfo.InvariantCulture, out double kilohertz))
            {
                result.Add(new MapNavaid(row[ident], row[name], kind, kilohertz, latitude, longitude));
            }
        }

        return result;
    }

    /// <summary>A minimal CSV reader: quotes, commas and line breaks inside fields.</summary>
    /// <param name="reader">The CSV.</param>
    internal static IEnumerable<string[]> Rows(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            char c = (char)ch;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                yield return [.. row];
                row.Clear();
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return [.. row];
        }
    }
}
