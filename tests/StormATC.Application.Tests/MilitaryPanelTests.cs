using StormATC.Application.Panel;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The military page of the in-simulator panel.</summary>
public class MilitaryPanelTests
{
    private static AtcSession Session(FlightRules rules, PhraseologyLanguage language = PhraseologyLanguage.French) =>
        AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightRules = rules,
        Language = language,
    };

    private static VfrAerodromeProfile Base() => new()
    {
        Icao = "LFBC",
        Name = "Cazaux",
        Frequencies =
        [
            new AerodromeFrequency("APP", Frequency.FromMegahertz(119.600m), "Transit"),
            new AerodromeFrequency("TWR", Frequency.FromMegahertz(118.400m), string.Empty),
        ],
    };

    [Theory]
    [InlineData(FlightRules.Ifr)]
    [InlineData(FlightRules.Vfr)]
    public void A_civil_flight_has_no_military_page(FlightRules rules) =>
        Assert.Null(ToolbarPanelStateBuilder.Build(Session(rules), voice: null, Base()).Military);

    [Theory]
    [InlineData(FlightRules.CamV, "CAM V")]
    [InlineData(FlightRules.CamI, "CAM I")]
    public void A_cam_flight_has_the_regime_the_base_and_its_frequencies(FlightRules rules, string regime)
    {
        ToolbarPanelMilitary military = ToolbarPanelStateBuilder.Build(Session(rules), voice: null, Base()).Military!;

        Assert.Equal(regime, military.Regime);
        Assert.Equal("Cazaux (LFBC)", military.Aerodrome);
        Assert.Equal(["APP", "TWR"], military.Frequencies.Select(f => f.Service));
        Assert.Contains("UHF", military.Labels.VhfOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void The_military_calls_cover_taxi_arrival_radar_and_regime_in_both_languages()
    {
        foreach (PhraseologyLanguage language in new[] { PhraseologyLanguage.French, PhraseologyLanguage.English })
        {
            IReadOnlyList<PhraseologySection> sections = PhraseologyCatalog.MilitarySections(language, "FOXTROT ALFA");

            Assert.Equal(["ground", "arrival", "radar", "regime"], sections.Select(s => s.Key));
            Assert.All(sections, section =>
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title));
                Assert.NotEmpty(section.Examples);
                Assert.All(section.Examples, example =>
                    Assert.False(string.IsNullOrWhiteSpace(example.Controller), example.Situation));
            });
        }
    }

    [Fact]
    public void A_radar_approach_in_progress_is_shown_on_the_page()
    {
        AtcSession session = Session(FlightRules.CamV).WithRadarApproach(new StormATC.Domain.Approach.RadarApproachState
        {
            Kind = StormATC.Domain.Approach.RadarApproachKind.Par,
            Runway = RunwayIdentifier.Create("29"),
            StartedAt = Fixture.Now,
            LastCallAt = Fixture.Now,
            Established = true,
        });

        ToolbarPanelMilitary military = ToolbarPanelStateBuilder.Build(session, voice: null, Base()).Military!;

        Assert.Equal("PAR piste 29 : contact radar", military.Radar);
    }
}
