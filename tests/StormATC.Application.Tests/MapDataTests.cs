using System.Text.Json;
using StormATC.Application.Map;

namespace StormATC.Application.Tests;

/// <summary>What the map reads from OpenAIP and OurAirports, and where its base map tiles come from.</summary>
public sealed class MapDataTests
{
    private const string Page = """
    { "totalPages": 3, "items": [
      { "name": "CTR TOUSSUS", "type": 4, "icaoClass": 3,
        "lowerLimit": { "value": 0, "unit": 1, "referenceDatum": 0 }, "upperLimit": { "value": 2500, "unit": 1, "referenceDatum": 1 },
        "geometry": { "type": "Polygon", "coordinates": [ [ [2.0, 48.0], [2.5, 48.0], [2.5, 48.5], [2.0, 48.5], [2.0, 48.0] ] ] } },
      { "name": "TMA HAUTE", "type": 7, "icaoClass": 0,
        "lowerLimit": { "value": 100, "unit": 6, "referenceDatum": 2 }, "upperLimit": { "value": 195, "unit": 6, "referenceDatum": 2 },
        "geometry": { "type": "MultiPolygon", "coordinates": [
          [ [ [1.0, 40.0], [1.5, 40.0], [1.5, 40.5], [1.0, 40.0] ] ],
          [ [ [3.0, 41.0], [3.5, 41.0], [3.5, 41.5], [3.0, 41.0] ] ] ] } },
      { "name": "sans contour", "type": 4 },
      { "name": "SECTEUR", "type": 33,
        "geometry": { "type": "Polygon", "coordinates": [ [ [0, 0], [1, 0], [1, 1], [0, 0] ] ] } }
    ] }
    """;

    private static List<MapAirspace> Read()
    {
        using JsonDocument document = JsonDocument.Parse(Page);
        return OpenAipAirspaces.ParsePage(document.RootElement, out _);
    }

    [Fact]
    public void A_page_gives_the_airspaces_with_a_polygon_with_their_limits_in_words_and_feet()
    {
        using JsonDocument document = JsonDocument.Parse(Page);
        List<MapAirspace> zones = OpenAipAirspaces.ParsePage(document.RootElement, out int pages);

        Assert.Equal(3, pages);
        Assert.Equal(["CTR TOUSSUS", "TMA HAUTE", "SECTEUR"], zones.Select(z => z.Name));

        MapAirspace ctr = zones[0];
        Assert.Equal("ctr", ctr.Kind);
        Assert.Equal("D", ctr.IcaoClass);
        Assert.Equal("CTR", ctr.Type);
        Assert.Equal(("SFC", 0.0), (ctr.Lower, ctr.LowerFt));
        Assert.Equal(("2500ft AMSL", 2500.0), (ctr.Upper, ctr.UpperFt));
        Assert.Equal([2.0, 48.0], ctr.Rings[0][0]);

        MapAirspace tma = zones[1];
        Assert.Equal(2, tma.Rings.Length);
        Assert.Equal(("FL100", 10000.0), (tma.Lower, tma.LowerFt));
        Assert.Equal(("FL195", 19500.0), (tma.Upper, tma.UpperFt));
    }

    [Fact]
    public void Sectors_and_the_airspaces_whose_floor_is_too_high_are_not_shown()
    {
        List<MapAirspace> zones = Read();

        Assert.True(OpenAipAirspaces.IsShown(zones[0], 11500));
        Assert.True(OpenAipAirspaces.IsShown(zones[1], 11500));       // floor FL100 = 10000 ft: below the limit
        Assert.False(OpenAipAirspaces.IsShown(zones[2], null));       // a sector
    }

    [Fact]
    public void The_floor_limit_hides_what_starts_above_it_and_null_shows_everything()
    {
        List<MapAirspace> zones = Read();
        MapAirspace high = zones[1] with { LowerFt = 15000 };

        Assert.False(OpenAipAirspaces.IsShown(high, 11500));
        Assert.True(OpenAipAirspaces.IsShown(high, null));
    }

    [Theory]
    [InlineData("clair", "", 0, 8, 129, 90, "World_Light_Gray_Base/MapServer/tile/8/90/129")]
    [InlineData("clair", "", 1, 8, 129, 90, "World_Light_Gray_Reference/MapServer/tile/8/90/129")]
    [InlineData("rues", "", 0, 19, 1, 1, "https://tile.openstreetmap.org/19/1/1.png")]
    [InlineData("carto-light", "KEY", 0, 5, 16, 11, "light_all/5/16/11.png?key=KEY")]
    [InlineData("carto-light", "", 0, 5, 16, 11, "World_Light_Gray_Base/MapServer/tile/5/11/16")]
    public void A_tile_comes_from_the_provider_of_the_map(string key, string carto, int layer, int z, int x, int y, string expected) =>
        Assert.Contains(expected, MapBaseMaps.TileUrl(key, carto, layer, z, x, y));

    [Theory]
    [InlineData("rues", 1, 5, 0, 0)]
    [InlineData("clair", 2, 5, 0, 0)]
    [InlineData("clair", 0, 17, 0, 0)]
    [InlineData("clair", 0, 3, 8, 0)]
    [InlineData("clair", 0, 3, -1, 0)]
    public void A_tile_outside_the_map_does_not_exist(string key, int layer, int z, int x, int y) =>
        Assert.Null(MapBaseMaps.TileUrl(key, string.Empty, layer, z, x, y));

    [Fact]
    public void An_unknown_map_or_a_carto_map_without_a_key_is_the_light_grey_map()
    {
        Assert.Equal("clair", MapBaseMaps.Effective("nonsense", string.Empty));
        Assert.Equal("clair", MapBaseMaps.Effective("carto-voyager", string.Empty));
        Assert.Equal("carto-voyager", MapBaseMaps.Effective("carto-voyager", "K"));
        Assert.Equal(2, MapBaseMaps.LayerCount("clair", string.Empty));
        Assert.Equal(1, MapBaseMaps.LayerCount("relief", string.Empty));
    }

    [Fact]
    public void The_aerodromes_and_the_beacons_of_a_country_are_read_from_the_csv_of_OurAirports()
    {
        const string airports = """
        id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,continent,iso_country
        1,LFPN,small_airport,"Toussus-le-Noble, Yvelines",48.7519,2.1061,538,EU,FR
        2,LFPG,large_airport,Paris Charles de Gaulle,49.0097,2.5479,392,EU,FR
        3,EGLL,large_airport,Heathrow,51.47,-0.4543,83,EU,GB
        4,LFXX,heliport,Un héliport,48.0,2.0,100,EU,FR
        5,LFYY,closed,Fermé,48.0,2.0,100,EU,FR
        """;
        const string navaids = """
        id,filename,ident,name,type,frequency_khz,latitude_deg,longitude_deg,elevation_ft,iso_country
        1,a,TSU,Toussus,VOR-DME,113500,48.75,2.11,500,FR
        2,b,PG,Paris,NDB,373,49.0,2.5,300,FR
        3,c,XYZ,Dme seul,DME,113500,48.0,2.0,300,FR
        4,d,LON,London,VOR,113600,51.0,0.0,300,GB
        """;

        List<MapAirport> parsedAirports = OurAirportsData.ParseAirports(new StringReader(airports), "fr");
        List<MapNavaid> parsedNavaids = OurAirportsData.ParseNavaids(new StringReader(navaids), "FR");

        Assert.Equal(["LFPN", "LFPG"], parsedAirports.Select(a => a.Ident));
        Assert.Equal("Toussus-le-Noble, Yvelines", parsedAirports[0].Name);
        Assert.Equal(48.7519, parsedAirports[0].Lat);
        Assert.Equal([("TSU", "vor", 113500.0), ("PG", "ndb", 373.0)], parsedNavaids.Select(n => (n.Ident, n.Kind, n.FrequencyKhz)));
    }
}
