using StormATC.Domain.Weather;

namespace StormATC.Domain.Tests;

/// <summary>Reading real METARs into their parts.</summary>
public class MetarParserTests
{
    private static DecodedMetar Parse(string raw)
    {
        Assert.True(MetarParser.TryParse(raw, out DecodedMetar metar), raw);
        return metar;
    }

    [Fact]
    public void A_european_report_is_read_group_by_group()
    {
        DecodedMetar metar = Parse("LFPO 271030Z AUTO 25012G22KT 220V290 9999 -RA FEW008 BKN014 OVC025 12/10 Q1008 NOSIG");

        Assert.Equal("LFPO", metar.Station);
        Assert.Equal((27, 10, 30), (metar.Day, metar.Hour, metar.Minute));
        Assert.True(metar.IsAutomatic);
        Assert.Equal(250, metar.WindDirectionDegrees);
        Assert.Equal(12, metar.WindSpeedKnots);
        Assert.Equal(22, metar.GustKnots);
        Assert.Equal((220, 290), (metar.WindVariationFromDegrees, metar.WindVariationToDegrees));
        Assert.Equal(9999, metar.VisibilityMeters);
        Assert.Equal(["-RA"], metar.Phenomena);
        Assert.Equal(
            [("FEW", 800), ("BKN", 1400), ("OVC", 2500)],
            metar.Clouds.Select(c => (c.Coverage, c.BaseFeet ?? -1)));
        Assert.Equal((12, 10), (metar.TemperatureCelsius, metar.DewPointCelsius));
        Assert.Equal(1008, metar.QnhHectopascals);
        Assert.Equal("NOSIG", metar.Trend);
    }

    [Fact]
    public void Cavok_and_a_negative_temperature_are_read()
    {
        DecodedMetar metar = Parse("METAR EGLL 101020Z 05003KT CAVOK M02/M04 Q1030=");

        Assert.True(metar.IsCavok);
        Assert.Equal(-2, metar.TemperatureCelsius);
        Assert.Equal(-4, metar.DewPointCelsius);
        Assert.Equal(1030, metar.QnhHectopascals);
        Assert.Equal(10_000, metar.ToWeatherSnapshot("test").VisibilityMeters);
    }

    [Fact]
    public void An_american_report_in_statute_miles_and_inches_is_converted()
    {
        DecodedMetar metar = Parse("KBOS 121654Z 18010KT 1 1/2SM BR OVC004 15/14 A2992 RMK AO2 SLP132");

        Assert.Equal(2414, metar.VisibilityMeters);
        Assert.Equal(1013, metar.QnhHectopascals);
        Assert.Equal(["BR"], metar.Phenomena);
        Assert.Equal("AO2 SLP132", metar.Remarks);
    }

    [Fact]
    public void Wind_in_metres_per_second_is_given_in_knots()
    {
        DecodedMetar metar = Parse("UUEE 121630Z 30005MPS 9999 NSC 05/01 Q1015");

        Assert.Equal(10, metar.WindSpeedKnots);
        Assert.True(metar.NoSignificantCloud);
    }

    [Fact]
    public void Variable_wind_has_no_direction_and_calm_wind_has_no_speed()
    {
        DecodedMetar variable = Parse("LFPG 121630Z VRB02KT 9999 SCT040 10/05 Q1015");
        DecodedMetar calm = Parse("LFPG 121630Z 00000KT 9999 SCT040 10/05 Q1015");

        Assert.True(variable.WindIsVariable);
        Assert.Null(variable.WindDirectionDegrees);
        Assert.Equal(0, calm.WindSpeedKnots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a metar")]
    [InlineData("LFPO")]
    public void Text_that_is_not_a_report_is_refused(string raw) =>
        Assert.False(MetarParser.TryParse(raw, out _));
}
