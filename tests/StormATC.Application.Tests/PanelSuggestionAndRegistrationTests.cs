using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Panel;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The suggested reply in the panel, the arrows it can draw, and a callsign that follows the registration.</summary>
public class PanelSuggestionAndRegistrationTests
{
    private static AtcSession SessionWithPlan(PhraseologyLanguage language = PhraseologyLanguage.French) =>
        AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        Language = language,
        FlightPlan = Plan("FHAST"),
    };

    private static FlightPlan Plan(string callsign) => new()
    {
        FlightPlanId = "p",
        Callsign = Callsign.Create(callsign),
        DepartureIcao = "STMR",
        DestinationIcao = "STMR",
        SourceIdentifier = "Test",
    };

    [Fact]
    public void The_suggestion_is_in_the_state_only_when_the_option_is_on()
    {
        AtcSession session = SessionWithPlan();

        Assert.Null(ToolbarPanelStateBuilder.Build(session).Suggestion);

        ToolbarPanelSuggestion suggestion = ToolbarPanelStateBuilder.Build(session, showSuggestion: true).Suggestion!;
        Assert.Equal("À dire", suggestion.Heading);
        Assert.False(string.IsNullOrWhiteSpace(suggestion.Title));
    }

    [Fact]
    public void The_suggestion_heading_follows_the_radio_language()
    {
        ToolbarPanelSuggestion english = ToolbarPanelStateBuilder
            .Build(SessionWithPlan(PhraseologyLanguage.English), showSuggestion: true).Suggestion!;

        Assert.Equal("What to say", english.Heading);
    }

    [Fact]
    public void The_chart_buttons_use_characters_the_simulators_browser_can_draw()
    {
        foreach (PhraseologyLanguage language in new[] { PhraseologyLanguage.French, PhraseologyLanguage.English })
        {
            ToolbarPanelChartLabels charts = ToolbarPanelStateBuilder.Build(SessionWithPlan(language)).Labels.Charts!;

            foreach (string text in new[] { charts.Back, charts.Previous, charts.Next })
            {
                Assert.DoesNotContain(text, character => character is '←' or '◀' or '▶');
            }
        }
    }

    [Fact]
    public void A_callsign_that_was_the_registration_follows_the_new_registration()
    {
        var context = NewContext();
        context.UpdateSession(SessionWithPlan() with { AircraftState = TestAerodromes.AtStand(Fixture.Now) with { AtcIdentifier = "F-HAST" } });

        context.UpdateAircraftState(TestAerodromes.AtStand(Fixture.Now) with { AtcIdentifier = "L-FTR" });

        Assert.Equal("LFTR", Callsign.Normalize(context.CurrentSession.Callsign!.Original));
        Assert.Equal("L-FTR", context.CurrentSession.FlightPlan!.Registration);
    }

    [Fact]
    public void A_callsign_that_is_not_the_registration_is_left_alone()
    {
        var context = NewContext();
        AtcSession session = SessionWithPlan() with { AircraftState = TestAerodromes.AtStand(Fixture.Now) with { AtcIdentifier = "F-ABCD" } };
        context.UpdateSession(session);

        context.UpdateAircraftState(TestAerodromes.AtStand(Fixture.Now) with { AtcIdentifier = "L-FTR" });

        Assert.Equal("FHAST", Callsign.Normalize(context.CurrentSession.Callsign!.Original));
    }

    private static AtcSessionContext NewContext() => new(
        new StubAirportDataProvider(),
        new StubTrafficProvider(),
        new StubWeatherProvider(),
        new FixedClock(Fixture.Now),
        NullLogger<AtcSessionContext>.Instance,
        vfrProfileProvider: null);
}
