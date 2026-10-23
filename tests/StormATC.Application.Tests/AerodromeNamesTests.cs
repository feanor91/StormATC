using StormATC.Application.Charts;

namespace StormATC.Application.Tests;

/// <summary>The aerodrome names that label the charts in the panel.</summary>
public class AerodromeNamesTests
{
    [Theory]
    [InlineData("LFPL", "Lognes")]
    [InlineData("lfsi", "Saint-Dizier")]
    public void A_known_aerodrome_has_a_name(string icao, string contains) =>
        Assert.Contains(contains, AerodromeNames.Find(icao), StringComparison.Ordinal);

    [Theory]
    [InlineData(null)]
    [InlineData("ZZZZ")]
    public void An_unknown_aerodrome_has_none(string? icao) => Assert.Null(AerodromeNames.Find(icao));

    [Fact]
    public void A_chart_carries_the_name_of_its_aerodrome()
    {
        var chart = new VacChart("AD-2.LFPL", "AD-2.LFPL", "LFPL");

        Assert.Contains("Lognes", chart.AerodromeName, StringComparison.Ordinal);
        Assert.Null(new VacChart("notes", "notes", null).AerodromeName);
    }
}
