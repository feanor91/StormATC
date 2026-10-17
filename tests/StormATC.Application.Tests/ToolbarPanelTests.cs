using StormATC.Application.Panel;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The state the in-simulator panel shows: the conversation and the phraseology guide.</summary>
public class ToolbarPanelTests
{
    private static AtcSession Session(
        FlightPhase phase = FlightPhase.Preflight,
        FlightRules rules = FlightRules.Ifr,
        PhraseologyLanguage language = PhraseologyLanguage.French)
        => AtcSession
            .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
            with
        {
            FlightPlan = new FlightPlan
            {
                FlightPlanId = "panel",
                Callsign = Callsign.Create("FGABC"),
                DepartureIcao = "LFPL",
                DestinationIcao = "LFPL",
                SourceIdentifier = "Test",
            },
            Phase = phase,
            FlightRules = rules,
            Language = language,
        };

    // =====================================================================================
    // Which stage the flight is in
    // =====================================================================================

    [Theory]
    [InlineData(FlightPhase.Preflight, FlightRules.Ifr, PhraseologyStage.Clearance)]
    [InlineData(FlightPhase.IFRClearanceIssued, FlightRules.Ifr, PhraseologyStage.Clearance)]
    [InlineData(FlightPhase.Preflight, FlightRules.Vfr, PhraseologyStage.VfrDeparture)]
    [InlineData(FlightPhase.Pushback, FlightRules.Ifr, PhraseologyStage.Pushback)]
    [InlineData(FlightPhase.TaxiOut, FlightRules.Vfr, PhraseologyStage.TaxiOut)]
    [InlineData(FlightPhase.HoldingShort, FlightRules.Ifr, PhraseologyStage.Departure)]
    [InlineData(FlightPhase.LineUp, FlightRules.Vfr, PhraseologyStage.Departure)]
    [InlineData(FlightPhase.Takeoff, FlightRules.Ifr, PhraseologyStage.Departure)]
    [InlineData(FlightPhase.InitialClimb, FlightRules.Ifr, PhraseologyStage.Climb)]
    [InlineData(FlightPhase.Enroute, FlightRules.Ifr, PhraseologyStage.Enroute)]
    [InlineData(FlightPhase.Descent, FlightRules.Ifr, PhraseologyStage.Descent)]
    [InlineData(FlightPhase.Approach, FlightRules.Ifr, PhraseologyStage.Approach)]
    [InlineData(FlightPhase.Final, FlightRules.Ifr, PhraseologyStage.Approach)]
    [InlineData(FlightPhase.GoAround, FlightRules.Ifr, PhraseologyStage.GoAround)]
    [InlineData(FlightPhase.Landed, FlightRules.Ifr, PhraseologyStage.TaxiIn)]
    [InlineData(FlightPhase.TaxiIn, FlightRules.Vfr, PhraseologyStage.TaxiIn)]
    [InlineData(FlightPhase.Circuit, FlightRules.Vfr, PhraseologyStage.VfrCircuit)]
    [InlineData(FlightPhase.InitialClimb, FlightRules.Vfr, PhraseologyStage.VfrCircuit)]
    [InlineData(FlightPhase.Final, FlightRules.Vfr, PhraseologyStage.VfrCircuit)]
    [InlineData(FlightPhase.Abnormal, FlightRules.Ifr, PhraseologyStage.Abnormal)]
    public void The_stage_follows_the_phase_and_the_flight_rules(
        FlightPhase phase, FlightRules rules, PhraseologyStage expected) =>
        Assert.Equal(expected, PhraseologyCatalog.StageFor(Session(phase, rules)));

    [Fact]
    public void Every_phase_has_a_stage()
    {
        foreach (FlightPhase phase in Enum.GetValues<FlightPhase>())
        {
            foreach (FlightRules rules in Enum.GetValues<FlightRules>())
            {
                PhraseologyStage stage = PhraseologyCatalog.StageFor(Session(phase, rules));
                Assert.Contains(stage, PhraseologyCatalog.Stages);
            }
        }
    }

    // =====================================================================================
    // The examples
    // =====================================================================================

    [Theory]
    [InlineData(PhraseologyLanguage.French)]
    [InlineData(PhraseologyLanguage.English)]
    public void Every_stage_has_examples_in_both_languages_that_name_the_aircraft(PhraseologyLanguage language)
    {
        foreach (PhraseologyStage stage in PhraseologyCatalog.Stages)
        {
            PhraseologyGuide guide = PhraseologyCatalog.For(stage, language, "FOXTROT ALFA");

            Assert.Equal(stage, guide.Stage);
            Assert.False(string.IsNullOrWhiteSpace(guide.Title));
            Assert.NotEmpty(guide.Examples);
            Assert.All(guide.Examples, example =>
            {
                Assert.False(string.IsNullOrWhiteSpace(example.Situation));
                Assert.Contains("FOXTROT ALFA", example.Pilot, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public void The_always_possible_calls_exist_in_both_languages()
    {
        Assert.NotEmpty(PhraseologyCatalog.Always(PhraseologyLanguage.French, "FOXTROT ALFA"));
        Assert.NotEmpty(PhraseologyCatalog.Always(PhraseologyLanguage.English, "FOXTROT ALFA"));
    }

    [Fact]
    public void The_french_and_english_guides_have_the_same_number_of_calls()
    {
        foreach (PhraseologyStage stage in PhraseologyCatalog.Stages)
        {
            Assert.Equal(
                PhraseologyCatalog.For(stage, PhraseologyLanguage.French, "X").Examples.Count,
                PhraseologyCatalog.For(stage, PhraseologyLanguage.English, "X").Examples.Count);
        }
    }

    // =====================================================================================
    // The state
    // =====================================================================================

    [Fact]
    public void The_state_names_the_flight_and_the_stage_in_the_radio_language()
    {
        AtcSession session = Session(FlightPhase.HoldingShort, FlightRules.Vfr) with
        {
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = Frequency.FromMegahertz(118.600m),
        };

        ToolbarPanelState state = ToolbarPanelStateBuilder.Build(session);

        Assert.Equal("fr", state.Language);
        Assert.Equal("FGABC", state.Callsign);
        Assert.Equal("VFR", state.Rules);
        Assert.Equal("118.600", state.Frequency);
        Assert.Equal("Tour", state.Controller);
        Assert.Equal("Departure", state.Phase.Key);
        Assert.Equal("Dialogue ATC", state.Labels.TabDialogue);
        Assert.Equal("Phraséologie", state.Labels.TabPhraseology);
        Assert.Equal(state.Phase.Title, state.Phraseology.Title);
    }

    [Fact]
    public void The_state_is_in_english_for_an_english_session()
    {
        ToolbarPanelState state = ToolbarPanelStateBuilder.Build(
            Session(language: PhraseologyLanguage.English));

        Assert.Equal("en", state.Language);
        Assert.Equal("ATC dialogue", state.Labels.TabDialogue);
        Assert.Equal("Phraseology", state.Labels.TabPhraseology);
    }

    [Fact]
    public void The_conversation_is_shown_in_order_with_who_spoke()
    {
        AtcSession session = Session()
            .WithConversationTurn(Turn(ConversationSpeaker.Pilot, "Lognes Sol, demande roulage", ControllerPosition.None))
            .WithConversationTurn(Turn(ConversationSpeaker.Controller, "Roulez point d'attente", ControllerPosition.Ground));

        ToolbarPanelState state = ToolbarPanelStateBuilder.Build(session);

        Assert.Collection(
            state.Turns,
            first =>
            {
                Assert.Equal("pilot", first.Speaker);
                Assert.Equal("Vous", first.Who);
                Assert.Equal("Lognes Sol, demande roulage", first.Text);
            },
            second =>
            {
                Assert.Equal("atc", second.Speaker);
                Assert.Equal("Sol", second.Who);
                Assert.Matches(@"^\d\d:\d\d$", second.Time);
            });
    }

    [Fact]
    public void Only_the_most_recent_transmissions_are_kept()
    {
        AtcSession session = Session();
        for (int i = 0; i < ToolbarPanelStateBuilder.MaximumTurns + 20; i++)
        {
            session = session.WithConversationTurn(
                Turn(ConversationSpeaker.Pilot, $"call {i}", ControllerPosition.None));
        }

        ToolbarPanelState state = ToolbarPanelStateBuilder.Build(session);

        Assert.Equal(ToolbarPanelStateBuilder.MaximumTurns, state.Turns.Count);
        Assert.Equal($"call {ToolbarPanelStateBuilder.MaximumTurns + 19}", state.Turns[^1].Text);
    }

    [Fact]
    public void Without_a_flight_plan_the_state_still_builds()
    {
        AtcSession session = Session() with { FlightPlan = null };

        ToolbarPanelState state = ToolbarPanelStateBuilder.Build(session);

        Assert.Equal(string.Empty, state.Callsign);
        Assert.NotEmpty(state.Phraseology.Examples);
    }

    private static ConversationTurn Turn(ConversationSpeaker speaker, string text, ControllerPosition controller) => new()
    {
        TurnId = Guid.CreateVersion7(),
        Timestamp = Fixture.Now,
        Speaker = speaker,
        Controller = controller,
        Text = text,
    };
}
