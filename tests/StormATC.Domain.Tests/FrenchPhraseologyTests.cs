using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// The French phraseology profile: DSNA numbers and wording, and the guarantee that switching
/// language changes words only, never decisions.
/// </summary>
public class FrenchPhraseologyTests
{
    private static readonly FrenchAtcPhraseology Fr = FrenchAtcPhraseology.Instance;
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    [Fact]
    public void Sending_a_pilot_to_another_controller_names_the_frequency()
    {
        Frequency tower = Frequency.FromMegahertz(118.700m);

        string withFrequency = Fr.ContactController(null, ControllerPosition.Tower, tower);
        string without = Fr.ContactController(null, ControllerPosition.Tower);

        Assert.Contains("contactez Tour", withFrequency, StringComparison.Ordinal);
        Assert.Contains(Fr.SpeakFrequency(tower), withFrequency, StringComparison.Ordinal);
        Assert.DoesNotContain("décimale", without, StringComparison.Ordinal);
        Assert.Contains(
            tower.ToSpoken(),
            EnglishAtcPhraseology.Instance.ContactController(null, ControllerPosition.Tower, tower),
            StringComparison.Ordinal);
    }

    // =====================================================================================
    // Numbers
    // =====================================================================================

    [Theory]
    [InlineData(0, "zéro")]
    [InlineData(21, "vingt et un")]
    [InlineData(71, "soixante et onze")]
    [InlineData(80, "quatre-vingts")]
    [InlineData(91, "quatre-vingt-onze")]
    [InlineData(180, "cent quatre-vingts")]
    [InlineData(200, "deux cents")]
    [InlineData(1013, "mille treize")]
    [InlineData(1022, "mille vingt-deux")]
    [InlineData(3500, "trois mille cinq cents")]
    [InlineData(80_000, "quatre-vingt mille")]
    [InlineData(200_000, "deux cent mille")]
    public void Cardinals_follow_everyday_French(int value, string expected) =>
        Assert.Equal(expected, FrenchSpokenNumbers.Cardinal(value));

    [Theory]
    [InlineData("3254", "trente-deux cinquante-quatre")]
    [InlineData("4271", "quarante-deux soixante et onze")]
    [InlineData("7000", "soixante-dix zéro zéro")]
    [InlineData("0451", "zéro quatre cinquante et un")]
    public void Transponder_codes_are_read_in_pairs(string code, string expected) =>
        Assert.Equal(expected, FrenchSpokenNumbers.Squawk(code));

    [Theory]
    [InlineData("133.375", "cent trente-trois décimale trois sept cinq")]
    [InlineData("132.000", "cent trente-deux décimale zéro")]
    [InlineData("118.700", "cent dix-huit décimale sept")]
    public void Frequencies_are_a_number_then_decimal_digits(string megahertz, string expected) =>
        Assert.Equal(
            expected,
            FrenchSpokenNumbers.Frequency(decimal.Parse(megahertz, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(5, "zéro zéro cinq")]
    [InlineData(90, "zéro quatre-vingt-dix")]
    [InlineData(270, "deux cent soixante-dix")]
    [InlineData(360, "trois cent soixante")]
    public void Headings_keep_three_figures_below_one_hundred(int degrees, string expected) =>
        Assert.Equal(expected, FrenchSpokenNumbers.Heading(degrees));

    [Fact]
    public void Runways_and_levels_are_spoken_the_French_way()
    {
        Assert.Equal("piste zéro cinq", Fr.SpeakRunway(RunwayIdentifier.Create("05")));
        Assert.Equal("piste vingt-sept gauche", Fr.SpeakRunway(RunwayIdentifier.Create("27L")));
        Assert.Equal("niveau cent vingt", Fr.SpeakAltitude(Altitude.FromFlightLevel(120)));
        Assert.Contains("trois mille", Fr.SpeakAltitude(Altitude.FromFeet(3000)), StringComparison.Ordinal);
        Assert.Contains("pieds", Fr.SpeakAltitude(Altitude.FromFeet(3000)), StringComparison.Ordinal);
    }

    // =====================================================================================
    // Messages
    // =====================================================================================

    [Fact]
    public void A_handoff_names_the_station_and_frequency_in_French()
    {
        string text = Fr.Handoff(
            Callsign.Create("STORM123"),
            ControllerPosition.Tower,
            Frequency.FromMegahertz(118.700m));

        Assert.Contains("contactez Tour cent dix-huit décimale sept", text, StringComparison.Ordinal);
        Assert.True(Fr.MentionsContact(text, ControllerPosition.Tower));
        Assert.False(Fr.MentionsContact(text, ControllerPosition.Ground));
    }

    [Fact]
    public void A_readback_correction_is_negative_with_the_missing_items()
    {
        string text = Fr.ReadbackCorrection(
            Callsign.Create("STORM123"),
            hasIncorrectValue: true,
            [Fr.ReadbackItemSquawk(SquawkCode.Create("4271"))]);

        Assert.Contains("négatif", text, StringComparison.Ordinal);
        Assert.Contains("transpondeur quarante-deux soixante et onze", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_code_round_trips()
    {
        Assert.Equal(PhraseologyLanguage.French, PhraseologyLanguages.Parse("fr-FR"));
        Assert.Equal(PhraseologyLanguage.French, PhraseologyLanguages.Parse("Français"));
        Assert.Equal(PhraseologyLanguage.English, PhraseologyLanguages.Parse("en"));
        Assert.Equal(PhraseologyLanguage.English, PhraseologyLanguages.Parse("klingon"));
        Assert.Equal(PhraseologyLanguage.English, PhraseologyLanguages.Parse(null));
        Assert.Equal("fr", PhraseologyLanguage.French.ToCode());
        Assert.Same(FrenchAtcPhraseology.Instance, AtcPhraseologies.For(PhraseologyLanguage.French));
        Assert.Same(EnglishAtcPhraseology.Instance, AtcPhraseologies.For(PhraseologyLanguage.English));
    }

    // =====================================================================================
    // Engine: same decisions, French words
    // =====================================================================================

    [Fact]
    public void A_French_IFR_clearance_carries_the_same_facts_as_the_English_one()
    {
        var squawk = SquawkCode.Create("4271");
        AtcSession english = TestScenario.NewSession();
        AtcSession french = english with { Language = PhraseologyLanguage.French };
        PilotIntent request = TestScenario.Intent(PilotIntentType.RequestIFRClearance, "request clearance");

        AtcDecision inEnglish = _engine.Evaluate(TestScenario.Context(english, request, preassignedSquawk: squawk));
        AtcDecision inFrench = _engine.Evaluate(TestScenario.Context(french, request, preassignedSquawk: squawk));

        Assert.True(inFrench.Accepted);
        Assert.Equal(inEnglish.ResponseType, inFrench.ResponseType);
        Assert.Equal(inEnglish.IssuedClearance?.Squawk, inFrench.IssuedClearance?.Squawk);
        Assert.Equal(inEnglish.IssuedClearance?.InitialAltitude, inFrench.IssuedClearance?.InitialAltitude);
        Assert.Equal(inEnglish.IssuedClearance?.Runway, inFrench.IssuedClearance?.Runway);
        Assert.Equal(inEnglish.ApprovedFacts.Keys.Order(), inFrench.ApprovedFacts.Keys.Order());

        string spoken = inFrench.FallbackPhraseology!;
        Assert.Contains("autorisé", spoken, StringComparison.Ordinal);
        Assert.Contains("piste dix-huit", spoken, StringComparison.Ordinal);
        Assert.Contains("transpondeur quarante-deux soixante et onze", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("squawk", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unrecognised_French_transmission_is_answered_in_French()
    {
        AtcSession session = TestScenario.NewSession() with { Language = PhraseologyLanguage.French };

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            session,
            TestScenario.Intent(PilotIntentType.Unknown, "il fait beau aujourd'hui")));

        Assert.NotNull(decision.FallbackPhraseology);
        Assert.Contains("répétez", decision.FallbackPhraseology, StringComparison.OrdinalIgnoreCase);
    }
}
