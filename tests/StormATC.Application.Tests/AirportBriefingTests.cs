using StormATC.Application.Atis;
using StormATC.Application.Weather;
using StormATC.Domain.Airports;
using StormATC.Domain.Phraseology;
using StormATC.Domain.ValueObjects;
using StormATC.Domain.Weather;

namespace StormATC.Application.Tests;

/// <summary>The weather and the ATIS of an aerodrome typed on the Radio page.</summary>
public class AirportBriefingTests
{
    private const string Report = "LFPO 271030Z 25012G22KT 9999 -RA BKN014 12/10 Q1008 NOSIG";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 35, 0, TimeSpan.Zero);

    private static DecodedMetar Decoded()
    {
        Assert.True(MetarParser.TryParse(Report, out DecodedMetar metar));
        return metar;
    }

    [Theory]
    [InlineData("LFPO", true)]
    [InlineData("lfpo", true)]
    [InlineData("LFP", false)]
    [InlineData("LFPOO", false)]
    [InlineData("LF-O", false)]
    [InlineData("", false)]
    public void An_icao_code_is_four_letters_or_digits(string icao, bool expected) =>
        Assert.Equal(expected, AirportBriefingService.IsValidIcao(icao));

    [Fact]
    public void The_report_is_described_in_french()
    {
        IReadOnlyList<MetarLine> lines = MetarDescriber.Describe(Decoded(), PhraseologyLanguage.French);

        Assert.Equal("250°, 12 kt, rafales 22 kt", lines.Single(l => l.Label == "Vent").Value);
        Assert.Equal("faible pluie", lines.Single(l => l.Label == "Temps présent").Value);
        Assert.Contains("fragmentés", lines.Single(l => l.Label == "Nuages").Value);
        Assert.Contains("1 400 ft", lines.Single(l => l.Label == "Nuages").Value);
        Assert.Equal("1008 hPa", lines.Single(l => l.Label == "QNH").Value);
    }

    [Fact]
    public void The_report_is_described_in_english()
    {
        IReadOnlyList<MetarLine> lines = MetarDescriber.Describe(Decoded(), PhraseologyLanguage.English);

        Assert.Equal("250° at 12 kt, gusting 22 kt", lines.Single(l => l.Label == "Wind").Value);
        Assert.Equal("light rain", lines.Single(l => l.Label == "Weather").Value);
        Assert.Equal("10 km or more", lines.Single(l => l.Label == "Visibility").Value);
    }

    [Fact]
    public void The_atis_is_made_from_the_report_with_the_frequency_typed()
    {
        AtisBroadcast atis = AtisBroadcastBuilder.BuildForAerodrome(
            "LFPO",
            Frequency.FromMegahertz(127.825m),
            PhraseologyLanguage.English,
            airport: null,
            Decoded().ToWeatherSnapshot("test"),
            Now);

        Assert.Equal("LFPO", atis.AirportIcao);
        Assert.Equal(Frequency.FromMegahertz(127.825m), atis.Frequency);
        Assert.Contains("Wind 250° at 12 kt", atis.DisplayText);
        Assert.Contains("QNH 1008", atis.DisplayText);
        Assert.Contains("Q N H", atis.SpokenText);
    }

    [Fact]
    public void The_atis_can_be_made_in_french_with_no_flight_plan_and_no_frequency()
    {
        AtisBroadcast atis = AtisBroadcastBuilder.BuildForAerodrome(
            "LFPO",
            frequency: null,
            PhraseologyLanguage.French,
            airport: null,
            Decoded().ToWeatherSnapshot("test"),
            Now);

        Assert.Contains("information", atis.SpokenText);
        Assert.Contains("Q N H", atis.SpokenText);
        Assert.Equal(PhraseologyLanguage.French, atis.Language);
    }
}

/// <summary>Finding aerodromes and services from the frequencies of the VAC charts.</summary>
public class AerodromeFrequencyLookupTests
{
    private static VfrAerodromeProfile Profile(string icao, string name, params (string Service, decimal Mhz)[] frequencies) =>
        new()
        {
            Icao = icao,
            Name = name,
            Frequencies =
            [
                .. frequencies.Select(f => new AerodromeFrequency(f.Service, Frequency.FromMegahertz(f.Mhz), string.Empty)),
            ],
        };

    private static readonly VfrAerodromeProfile[] Profiles =
    [
        Profile("LFAT", "Le Touquet", ("ATIS", 123.13m), ("APP", 120.275m), ("TWR", 118.455m)),
        Profile("LFAB", "Dieppe", ("A/A", 119.005m)),
        Profile("LFAC", "Calais", ("A/A", 119.005m)),
    ];

    [Fact]
    public void A_frequency_gives_the_aerodrome_and_the_service()
    {
        IReadOnlyList<AerodromeFrequencyMatch> matches =
            AerodromeFrequencyLookup.FindByFrequency(Profiles, Frequency.FromMegahertz(123.13m));

        AerodromeFrequencyMatch match = Assert.Single(matches);
        Assert.Equal(("LFAT", "ATIS"), (match.Icao, match.Service));
    }

    [Fact]
    public void A_shared_frequency_gives_every_aerodrome_that_uses_it() =>
        Assert.Equal(
            ["LFAB", "LFAC"],
            AerodromeFrequencyLookup.FindByFrequency(Profiles, Frequency.FromMegahertz(119.005m)).Select(m => m.Icao));

    [Fact]
    public void The_atis_frequency_is_the_one_the_chart_prints()
    {
        Assert.Equal(Frequency.FromMegahertz(123.13m), AerodromeFrequencyLookup.AtisOf(Profiles[0]));
        Assert.Null(AerodromeFrequencyLookup.AtisOf(Profiles[1]));
        Assert.Null(AerodromeFrequencyLookup.AtisOf(null));
    }

    [Fact]
    public void The_frequencies_of_an_aerodrome_fit_on_one_line() =>
        Assert.Equal(
            "ATIS 123.130 · APP 120.275 · TWR 118.455",
            AerodromeFrequencyLookup.Summarize(Profiles[0]));
}
