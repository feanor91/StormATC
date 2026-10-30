using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>Choosing a runway from the ones a VAC chart prints, as an ATIS must always name one.</summary>
public class VfrRunwaySelectionTests
{
    private static VfrAerodromeProfile Dieppe(int? preferredQfu = null, double? variation = 1.0) =>
        new()
        {
            Icao = "LFAB",
            PreferredQfu = preferredQfu,
            MagneticVariationDegrees = variation,
            Runways =
            [
                new(RunwayIdentifier.Create("13"), 126),
                new(RunwayIdentifier.Create("31"), 306),
            ],
        };

    [Theory]
    [InlineData(310, 12, "31")]
    [InlineData(130, 12, "13")]
    [InlineData(250, 12, "31")]
    [InlineData(40, 12, "13")]
    public void The_runway_most_into_the_wind_is_chosen(int windFromTrue, int speed, string expected) =>
        Assert.Equal(RunwayIdentifier.Create(expected), Dieppe().SelectRunway(windFromTrue, speed));

    [Fact]
    public void The_wind_is_turned_from_true_to_magnetic_before_it_is_compared()
    {
        // 218 true is 217 magnetic at 1 degree east: 126 and 306 are 91 and 89 degrees away, so 31 wins narrowly.
        Assert.Equal(RunwayIdentifier.Create("31"), Dieppe().SelectRunway(218, 10));
    }

    [Fact]
    public void A_light_wind_leaves_the_choice_to_the_preferred_direction()
    {
        Assert.Equal(RunwayIdentifier.Create("31"), Dieppe(preferredQfu: 306).SelectRunway(130, 2));
        Assert.Equal(RunwayIdentifier.Create("31"), Dieppe(preferredQfu: 306).SelectRunway(null, null));
    }

    [Fact]
    public void With_no_preference_and_no_wind_the_first_runway_is_named() =>
        Assert.Equal(RunwayIdentifier.Create("13"), Dieppe().SelectRunway(null, 0));

    [Fact]
    public void A_profile_without_runways_names_none() =>
        Assert.Null(new VfrAerodromeProfile { Icao = "LFXX" }.SelectRunway(200, 10));
}
