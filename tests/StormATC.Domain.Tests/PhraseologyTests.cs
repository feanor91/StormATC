using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// Deterministic phraseology: what a controller actually says.
/// </summary>
/// <remarks>
/// This is the output used whenever the language model is absent, unavailable, or produces
/// something that fails validation — which means it is the output most users will hear most of
/// the time. It has to be correct on its own, not merely a placeholder.
/// </remarks>
public class DeterministicPhraseologyTests
{
    private static readonly Callsign Storm123 = Callsign.Create("STORM123");

    [Fact]
    public void An_IFR_clearance_reads_out_every_element_in_order()
    {
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.IfrClearance,
            IssuedBy = ControllerPosition.ClearanceDelivery,
            IssuedAt = TestScenario.Now,
            ClearedToDestination = "STMD",
            Runway = RunwayIdentifier.Create("18"),
            DepartureProcedure = "STORM1A",
            InitialAltitude = Altitude.FromFeet(7_000),
            Squawk = SquawkCode.Create("4271"),
            NextFrequency = Frequency.FromMegahertz(119.100m),
        };

        string spoken = DeterministicPhraseology.IfrClearance(Storm123, clearance);

        Assert.StartsWith("Storm one two three", spoken, StringComparison.Ordinal);
        Assert.Contains("seven thousand", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("four two seven one", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one eight", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numbers_are_never_spoken_as_bare_digits()
    {
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.IfrClearance,
            IssuedBy = ControllerPosition.ClearanceDelivery,
            IssuedAt = TestScenario.Now,
            ClearedToDestination = "STMD",
            Runway = RunwayIdentifier.Create("18"),
            InitialAltitude = Altitude.FromFeet(7_000),
            Squawk = SquawkCode.Create("4271"),
        };

        string spoken = DeterministicPhraseology.IfrClearance(Storm123, clearance);

        Assert.DoesNotContain("4271", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("7000", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void A_takeoff_clearance_includes_the_wind_only_when_it_is_known()
    {
        RunwayIdentifier runway = RunwayIdentifier.Create("18");

        string withWind = DeterministicPhraseology.TakeoffClearance(
            Storm123, runway, Heading.FromDegrees(190), windSpeedKnots: 8, initialHeading: null);

        string withoutWind = DeterministicPhraseology.TakeoffClearance(
            Storm123, runway, windDirection: null, windSpeedKnots: null, initialHeading: null);

        Assert.Contains("wind", withWind, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wind", withoutWind, StringComparison.OrdinalIgnoreCase);

        // Both must still authorise the takeoff.
        Assert.Contains("cleared for takeoff", withWind, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleared for takeoff", withoutWind, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approach_clearance_never_says_cleared_to_land()
    {
        string spoken = DeterministicPhraseology.ApproachClearance(
            Storm123, "ILS", RunwayIdentifier.Create("36"));

        Assert.Contains("cleared", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cleared to land", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_taxi_clearance_names_the_route_and_the_hold_short()
    {
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Taxi,
            IssuedBy = ControllerPosition.Ground,
            IssuedAt = TestScenario.Now,
            TaxiRoute = ["A", "C"],
            TaxiDestination = "holding point runway 18",
            HoldShortOfRunway = RunwayIdentifier.Create("18"),
        };

        string spoken = DeterministicPhraseology.TaxiClearance(Storm123, clearance);

        Assert.Contains("alfa", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("charlie", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hold short", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Alphanumeric_taxiways_are_spoken_phonetically_while_named_surfaces_remain_words()
    {
        string spoken = DeterministicPhraseology.SpellTaxiways(["apron", "A6", "A10", "inner"]);

        Assert.Equal("apron, Alfa six, Alfa one zero, inner", spoken);
    }

    [Fact]
    public void A_handoff_gives_the_controller_and_the_frequency()
    {
        string spoken = DeterministicPhraseology.Handoff(
            Storm123, ControllerPosition.Ground, Frequency.FromMegahertz(121.700m));

        Assert.Contains("ground", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one two one decimal seven", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unable_response_gives_the_reason()
    {
        string spoken = DeterministicPhraseology.Unable(Storm123, "traffic on the runway");

        Assert.Contains("unable", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("traffic on the runway", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Say_again_works_without_a_callsign()
    {
        string spoken = DeterministicPhraseology.SayAgain(callsign: null);

        Assert.Contains("say again", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_mayday_is_acknowledged_differently_from_a_pan_pan()
    {
        Assert.NotEqual(
            DeterministicPhraseology.EmergencyAcknowledgement(Storm123, isMayday: true),
            DeterministicPhraseology.EmergencyAcknowledgement(Storm123, isMayday: false));
    }

    [Fact]
    public void Controller_names_are_the_ones_spoken_on_the_radio()
    {
        Assert.Equal("Delivery", DeterministicPhraseology.SpokenControllerName(ControllerPosition.ClearanceDelivery));
        Assert.Equal("Ground", DeterministicPhraseology.SpokenControllerName(ControllerPosition.Ground));
        Assert.Equal("Tower", DeterministicPhraseology.SpokenControllerName(ControllerPosition.Tower));
    }
}

/// <summary>The ICAO alphabet, in both directions.</summary>
public class PhoneticAlphabetTests
{
    [Fact]
    public void Spelling_uses_the_ICAO_forms()
    {
        string spelled = PhoneticAlphabet.SpellOut("STMR");

        Assert.Contains("Sierra", spelled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tango", spelled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mike", spelled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Romeo", spelled, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ICAO_spellings_of_A_and_J_are_used_outbound()
    {
        string spelled = PhoneticAlphabet.SpellOut("AJ");

        Assert.Contains("Alfa", spelled, StringComparison.Ordinal);
        Assert.Contains("Juliett", spelled, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alpha bravo", "AB")]
    [InlineData("alfa bravo", "AB")]
    [InlineData("juliet kilo", "JK")]
    [InlineData("juliett kilo", "JK")]
    public void Common_alternative_spellings_are_accepted_inbound(string text, string expected)
    {
        // Pilots say "alpha" and "juliet" far more often than the ICAO spellings. Refusing
        // those would reject correct readbacks.
        Assert.Equal(expected, PhoneticAlphabet.ExtractLetters(text));
    }

    [Fact]
    public void ExtractLetters_ignores_words_that_are_not_letters()
    {
        Assert.Equal("AC", PhoneticAlphabet.ExtractLetters("taxi via alfa and charlie"));
    }

    [Theory]
    [InlineData("apron, Alfa six", "apron,A6")]
    [InlineData("Alfa six and Bravo two", "A6,B2")]
    [InlineData("A6, Charlie", "A6,C")]
    [InlineData("Alfa and Charlie", "A,C")]
    public void Complete_taxiway_identifiers_are_extracted(
        string text,
        string expected)
    {
        Assert.Equal(
            expected,
            string.Join(",", PhoneticAlphabet.ExtractTaxiwayIdentifiers(text)));
    }
}

/// <summary>Spoken-number rendering and parsing.</summary>
public class SpokenNumbersTests
{
    [Theory]
    [InlineData(7_000, "seven thousand")]
    [InlineData(3_500, "three thousand five hundred")]
    [InlineData(12_000, "one two thousand")]
    [InlineData(500, "five hundred")]
    public void Altitudes_are_spoken_the_way_controllers_say_them(int feet, string expected) =>
        Assert.Equal(expected, SpokenNumbers.Altitude(feet));

    [Theory]
    [InlineData("4271", "four two seven one")]
    [InlineData("18", "one eight")]
    public void Digit_runs_are_spoken_individually(string digits, string expected) =>
        Assert.Equal(expected, SpokenNumbers.DigitsIndividually(digits));

    [Fact]
    public void Frequencies_use_decimal_and_individual_digits()
    {
        Assert.Equal("one two one decimal seven", SpokenNumbers.Frequency(121.700m));
    }

    [Fact]
    public void A_readback_containing_several_numbers_yields_only_the_altitude()
    {
        // The whole point of the scoping: a full clearance readback has a runway, an altitude,
        // and a squawk in one breath. Only the altitude phrase is an altitude.
        int? feet = SpokenNumbers.ParseSpokenAltitude(
            "runway one eight climb seven thousand squawk four two seven one storm one two three");

        Assert.Equal(7_000, feet);
    }

    [Fact]
    public void A_transmission_with_no_altitude_phrase_yields_nothing()
    {
        Assert.Null(SpokenNumbers.ParseSpokenAltitude("runway one eight squawk four two seven one"));
    }

    [Theory]
    [InlineData("descend two thousand five hundred", 2_500)]
    [InlineData("climb and maintain seven thousand feet", 7_000)]
    [InlineData("7000", 7_000)]
    public void Altitude_phrases_parse_in_context(string text, int expected) =>
        Assert.Equal(expected, SpokenNumbers.ParseSpokenAltitude(text));

    [Fact]
    public void Digit_words_fold_into_digits()
    {
        // Token spacing is preserved; collapsing it is the callsign normaliser's job.
        Assert.Equal("storm 1 2 3", SpokenNumbers.FoldDigitWords("storm one two three"));
    }

    [Theory]
    [InlineData("cleared to storm delta")]
    [InlineData("ready for departure")]
    public void Homophones_are_not_mistaken_for_digits(string text)
    {
        // "to" and "for" are only digits beside other digits. Folding them everywhere would
        // turn "ready for departure" into "ready 4 departure".
        string folded = SpokenNumbers.FoldDigitWords(text);

        Assert.DoesNotContain('2', folded);
        Assert.DoesNotContain('4', folded);
    }

    [Fact]
    public void Homophones_beside_real_digits_do_fold()
    {
        Assert.Equal("4 2", SpokenNumbers.FoldDigitWords("for two"));
    }

    [Theory]
    [InlineData("PILU2F", "PILU two Foxtrot")]
    [InlineData("STORM1A", "STORM one Alfa")]
    [InlineData("VEVA7R", "VEVA seven Romeo")]
    [InlineData("RADAR", "RADAR")]
    public void A_procedure_name_is_said_as_a_word_a_digit_and_a_letter(string procedure, string expected) =>
        Assert.Equal(expected, DeterministicPhraseology.SpeakProcedure(procedure));

    [Fact]
    public void The_clearance_says_the_departure_procedure_the_way_it_is_spoken()
    {
        var clearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.IfrClearance,
            IssuedBy = ControllerPosition.ClearanceDelivery,
            IssuedAt = TestScenario.Now,
            ClearedToDestination = "LFMN",
            DepartureProcedure = "PILU2F",
        };

        string spoken = DeterministicPhraseology.IfrClearance(Callsign.Create("STORM123"), clearance);

        Assert.Contains("PILU two Foxtrot departure", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("PILU2F", spoken, StringComparison.Ordinal);
    }
}
